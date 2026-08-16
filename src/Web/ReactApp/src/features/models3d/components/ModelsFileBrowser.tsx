import { useCallback, useMemo, useState, useRef } from 'react';
import { FileBrowser, type FileBrowserHandle } from '@/features/fileBrowser/components/FileBrowser';
import {
  type ColumnDef,
  type FileItem,
  type FileQueryState,
  type UseFileBrowserConfig,
} from '@/features/fileBrowser/types';
import { ModelUploadModal } from '@/common/components/modals/ModelUploadModal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { PrintablesBrowserModal } from '@/features/models3d/components/PrintablesBrowserModal';
import { PrintablesImportModal } from '@/features/models3d/components/PrintablesImportModal';
import { Button, TagChip } from '@/common/components/ui';
import { PrintablesIcon } from '@/common/components/icons/PrintablesIcon';
import { TagIcon, UploadIcon, EyeIcon, LayersTripleOutlineIcon, FilterIcon, DownloadIcon, DeleteIcon, FolderPlusIcon } from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import type { Model, Model3DSearchResponse } from '@/types/models';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';

const toFileItem = (model: Model): FileItem => ({
  id: model.id,
  path: model.path,
  fileName: model.name,
  isDirectory: false,
  fileSize: model.fileSize,
  uploadedAt: model.uploadedAt,
  tags: model.tags?.map((tag) => ({ id: tag.id, name: tag.name, color: tag.color })),
  thumbnailUrl: model.thumbnailUrl,
  meta: { model3d: model as unknown as Record<string, unknown> },
});

const formatBytes = (bytes?: number) => {
  if (!bytes) return '—';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${(bytes / Math.pow(k, i)).toFixed(1)} ${sizes[i]}`;
};

const modelColumns: ColumnDef[] = [
  { key: 'fileName', label: 'Name', sortable: true },
  {
    key: 'fileType',
    label: 'Type',
    render: (file) => {
      const model = file.meta?.model3d as Model | undefined;
      return model?.fileType ? model.fileType.toUpperCase() : '—';
    },
  },
  {
    key: 'fileSize',
    label: 'Size',
    sortable: true,
    align: 'right',
    render: (file) => formatBytes(file.fileSize),
  },
  {
    key: 'uploadedAt',
    label: 'Uploaded',
    sortable: true,
    render: (file) => (file.uploadedAt ? new Date(file.uploadedAt).toLocaleString() : '—'),
  },
  {
    key: 'thumbnailUrl',
    label: 'Thumbnail',
    render: (file) => {
      const model = file.meta?.model3d as Model | undefined;
      return model?.thumbnailUrl ? (
        <img
          src={model.thumbnailUrl}
          alt={file.fileName}
          className="h-12 w-12 rounded-sm object-cover"
        />
      ) : (
        '—'
      );
    },
  },
  {
    key: 'tags',
    label: 'Tags',
    render: (file) =>
      file.tags?.length ? (
        <div className="flex flex-wrap gap-1" aria-label="Tags">
          {file.tags.map((tag) => (
            <TagChip
              key={tag.id}
              label={tag.name}
              color={tag.color}
              truncate
            />
          ))}
        </div>
      ) : (
        '—'
      ),
  },
];

interface ModelsFileBrowserProps {
  viewMode?: 'grid' | 'explorer';
  onViewModeChange?: (mode: 'grid' | 'explorer') => void;
  selectedTags?: string[];
  onDeleteModels?: (modelIds: string[]) => Promise<void>;
  onShowTagModal?: () => void;
  onShowSingleTagModal?: (model: Model) => void;
  onToggleTagFilterPanel?: () => void;
  selectedModelIds?: string[];
  onSelectionChange?: (ids: string[]) => void;
  onOpenModel?: (model: Model) => void;
  onSliceModel?: (model: Model) => void;
  /**
   * When provided (non-null), restricts the browser to only these model ids, filtered
   * client-side (the /3d-models/query endpoint has no collection/id filter, #846). Pass
   * `null`/`undefined` to show all models.
   */
  collectionModelIds?: string[] | null;
  /** Shown alongside the tag bulk-action button when there is a selection. */
  onShowCollectionModal?: () => void;
}

/** Page size used to fetch models when a collection filter is active, so the client-side
 * id filter operates over the whole collection rather than one server page at a time. */
const COLLECTION_FILTER_PAGE_SIZE = 500;

/**
 * Normalizes every response shape `/3d-models/query` may return into a single paged
 * shape. Some deployments (e.g. the slicer module disabled) return a bare `[]` instead
 * of the `{ models, totalPages, ... }` envelope, and any response can otherwise omit
 * fields. Both the collection-filtering loop and the plain query path must go through
 * this before reading `models` or `totalPages`, so neither ever assumes the "full"
 * shape is present.
 */
function normalizeModelsSearchResponse(response: unknown): {
  models: Model[];
  totalCount: number;
  totalPages: number;
  page: number;
} {
  if (Array.isArray(response)) {
    return { models: response as Model[], totalCount: response.length, totalPages: 1, page: 1 };
  }

  const searchResponse = response as Partial<Model3DSearchResponse> | null | undefined;
  const models = Array.isArray(searchResponse?.models) ? searchResponse.models : [];
  return {
    models,
    totalCount: searchResponse?.totalCount ?? models.length,
    totalPages: searchResponse?.totalPages ?? 1,
    page: searchResponse?.page ?? 1,
  };
}

/** Params sent to the `/3d-models/query` endpoint (mirrors `apiClient.get3DModelsQuery`'s
 * request shape). Kept separate from `ModelsQueryKeyParams` below so the collection
 * discriminator can never leak into the network request. */
interface ModelsApiQueryParams {
  query?: string;
  tagIds?: string[];
  page: number;
  pageSize: number;
  sortBy: 'name' | 'size' | 'uploadedAt';
  descending: boolean;
}

/**
 * `mapQueryParams` return type used by `useFileBrowser` to build both the fetcher input
 * *and* the React Query cache key (`JSON.stringify(actualQueryParams)`, see useFileBrowser.ts).
 * `collectionMembershipKey` rides along only to keep the cache key in sync with which
 * collection - and which of its members - are active; it is stripped before the request
 * reaches the backend (see `fetcher` below) since `/3d-models/query` has no such field.
 */
interface ModelsQueryKeyParams extends ModelsApiQueryParams {
  /**
   * Stable discriminator for the active collection's membership, so switching collections
   * (or a collection's membership resolving from `[]` to its real ids after
   * `useModelCollectionMembers` loads) always produces a new cache key instead of silently
   * reusing another collection's - or the loading placeholder's - cached results (#846).
   * `undefined` when no collection filter is active (matches the "all models" query key).
   */
  collectionMembershipKey?: string;
}

export const ModelsFileBrowser = ({
  viewMode,
  onViewModeChange,
  selectedTags = [],
  onShowTagModal,
  onShowSingleTagModal,
  onToggleTagFilterPanel,
  selectedModelIds,
  onSelectionChange,
  onOpenModel,
  onSliceModel,
  collectionModelIds,
  onShowCollectionModal,
}: ModelsFileBrowserProps) => {
  const { hasPermission } = useAuth();
  const [showUploadModal, setShowUploadModal] = useState(false);
  const [showPrintablesBrowserModal, setShowPrintablesBrowserModal] = useState(false);
  const [showPrintablesImportModal, setShowPrintablesImportModal] = useState(false);
  const [selectedPrintablesUrl, setSelectedPrintablesUrl] = useState<string | null>(null);
  const [localSelection, setLocalSelection] = useState<string[]>([]);
  const fileBrowserRef = useRef<FileBrowserHandle>(null);

  const handleSliceModel = useCallback(
    (model: Model) => {
      if (onSliceModel) {
        onSliceModel(model);
      } else {
        window.location.assign(`/slicer?modelId=${model.id}`);
      }
    },
    [onSliceModel]
  );

  const handleDownload = useCallback(async (file: FileItem) => {
    if (file.isDirectory) return;
    const model3dFile = file.meta?.model3d as { name?: string } | undefined;
    const originalName = model3dFile?.name || file.fileName;

    try {
      await apiClient.downloadModel3dFile(file.id, originalName || '3d-model');
    } catch {
      toast.error('Failed to download model');
    }
  }, []);

  const [deleteConfirm, setDeleteConfirm] = useState<{ isOpen: boolean; file: FileItem | null }>({
    isOpen: false,
    file: null,
  });

  const handleDeleteClick = useCallback((file: FileItem) => {
    setDeleteConfirm({ isOpen: true, file });
  }, []);

  const handleDeleteConfirm = useCallback(async () => {
    if (!deleteConfirm.file) return;
    const file = deleteConfirm.file;
    setDeleteConfirm({ isOpen: false, file: null });
    try {
      await apiClient.deleteModel3dFile(file.id);
      toast.success('Model deleted successfully');
      await fileBrowserRef.current?.refetch();
    } catch (error) {
      toast.error('Failed to delete model');
      console.error('Delete error:', error);
    }
  }, [deleteConfirm.file]);

  const handleDeleteCancel = useCallback(() => {
    setDeleteConfirm({ isOpen: false, file: null });
  }, []);

  const selection = selectedModelIds ?? localSelection;

  const handleSelectionChange = useCallback(
    (ids: string[]) => {
      if (!selectedModelIds) {
        setLocalSelection(ids);
      }
      onSelectionChange?.(ids);
    },
    [onSelectionChange, selectedModelIds]
  );

  const sortOptions = useMemo(
    () => [
      { value: 'name', label: 'Name' },
      { value: 'size', label: 'Size' },
      { value: 'date', label: 'Date' },
    ],
    []
  );

  const mapQueryParams = useCallback(
    (query: FileQueryState): ModelsQueryKeyParams => ({
      query: query.search || undefined,
      tagIds: selectedTags.length ? selectedTags : undefined,
      page: collectionModelIds ? 1 : query.page,
      pageSize: collectionModelIds ? COLLECTION_FILTER_PAGE_SIZE : query.pageSize,
      sortBy: query.sortBy === 'name' ? 'name' : query.sortBy === 'size' ? 'size' : 'uploadedAt',
      descending: query.sortOrder === 'desc',
      // Sorted so the key is stable regardless of membership ordering; `undefined` (not an
      // empty string) when there is no active collection filter, so it never collides with
      // an active-but-empty collection's key.
      collectionMembershipKey: collectionModelIds ? [...collectionModelIds].sort().join(',') : undefined,
    }),
    [selectedTags, collectionModelIds]
  );

  const fetcher = useCallback(
    async (params: unknown) => {
      // Drop the query-key-only discriminator before it can reach the backend - only
      // `payload` (never the raw params) is ever passed to `apiClient.get3DModelsQuery`.
      // eslint-disable-next-line @typescript-eslint/no-unused-vars
      const { collectionMembershipKey, ...payload } = params as ModelsQueryKeyParams;

      if (collectionModelIds) {
        // An empty collection has no members to look up - skip the network round-trip
        // entirely rather than issuing a query guaranteed to return zero matches.
        if (collectionModelIds.length === 0) {
          return { items: [], totalItems: 0, totalPages: 1, totalSize: 0, page: 1 };
        }

        // The /3d-models/query endpoint has no collection/id-list filter (#846), so the
        // client-side id filter must page through the *entire* (search/tag-filtered) result
        // set - stopping after a single page would silently drop collection members that
        // fall on later pages.
        const idSet = new Set(collectionModelIds);
        const matches: Model[] = [];
        const foundIds = new Set<string>();
        let page = 1;
        let totalPages: number;
        do {
          const response = await apiClient.get3DModelsQuery({
            ...payload,
            page,
            pageSize: COLLECTION_FILTER_PAGE_SIZE,
          });
          const { models, totalPages: responseTotalPages } = normalizeModelsSearchResponse(response);
          totalPages = responseTotalPages || 1;
          for (const model of models) {
            if (idSet.has(model.id) && !foundIds.has(model.id)) {
              foundIds.add(model.id);
              matches.push(model);
            }
          }
          page += 1;
        } while (page <= totalPages && foundIds.size < idSet.size);

        const totalSize = matches.reduce((sum, model) => sum + (model.fileSize || 0), 0);
        return {
          items: matches,
          totalItems: matches.length,
          totalPages: 1,
          totalSize,
          page: 1,
        };
      }

      const response = await apiClient.get3DModelsQuery(payload);

      // Normalize before reading `models`/`totalPages` - the endpoint can return a bare
      // `[]` (e.g. slicer module disabled) instead of the full paged envelope.
      const { models, totalCount, totalPages, page: responsePage } = normalizeModelsSearchResponse(response);
      const totalSize = models.reduce((sum: number, model: Model) => sum + (model.fileSize || 0), 0);

      return {
        items: models,
        totalItems: totalCount,
        totalPages,
        totalSize,
        page: responsePage,
      };
    },
    [collectionModelIds]
  );

  const config: UseFileBrowserConfig<Model> = useMemo(
    () => ({
      fetcher,
      mapQueryParams,
      mapDomainToFileItem: toFileItem,
      defaultSort: { sortBy: 'date', sortOrder: 'desc' },
      selectedIds: selection,
      onSelectionChange: handleSelectionChange,
      initialPath: '/',
    }),
    [fetcher, handleSelectionChange, mapQueryParams, selection]
  );

  const renderActions = useCallback(
    (file: FileItem) => {
      if (file.isDirectory) return null;
      
      const model = file.meta?.model3d as Model | undefined;
      if (!model) return null;

      return (
        <>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={() => handleDownload(file)}
            title="Download file"
          >
            <DownloadIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={() => onShowSingleTagModal?.(model)}
            aria-label={`Tag ${file.fileName}`}
          >
            <TagIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={() => onOpenModel?.(model)}
            aria-label={`Open ${file.fileName}`}
          >
            <EyeIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="sm"
            variant="primary"
            onClick={() => handleSliceModel(model)}
            aria-label={`Slice ${file.fileName}`}
            iconCenter={<LayersTripleOutlineIcon className="h-4 w-4" />}
          />
          <Button
            type="button"
            size="sm"
            variant="secondary"
            className="text-pf-error hover:text-pf-error hover:bg-pf-error/10"
            onClick={() => handleDeleteClick(file)}
            title="Delete file"
          >
            <DeleteIcon className="h-4 w-4" />
          </Button>
        </>
      );
    },
    [handleSliceModel, onOpenModel, onShowSingleTagModal, handleDownload, handleDeleteClick]
  );

  const extraToolbarActions = (
    <>
      {onToggleTagFilterPanel && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          onClick={onToggleTagFilterPanel}
          title="Filter by tags"
          iconCenter={<FilterIcon className="h-4 w-4" />}
        />
      )}
      {hasPermission('3d_models', 'create') && (
        <Button
          type="button"
          onClick={() => setShowPrintablesBrowserModal(true)}
          variant="secondary"
          size="sm"
          title="Import from Printables"
          iconLeft={<PrintablesIcon />}
        >
          Printables
        </Button>
      )}
      {hasPermission('3d_models', 'create') && (
        <Button
          type="button"
          onClick={() => setShowUploadModal(true)}
          variant="secondary"
          size="sm"
          title="Upload models"
          iconCenter={<UploadIcon className="h-4 w-4" />}
        />
      )}
      {selection.length > 0 && onShowTagModal && (
        <Button
          type="button"
          onClick={onShowTagModal}
          variant="secondary"
          size="sm"
          title="Tag selected models"
          iconLeft={<TagIcon className="h-4 w-4" />}
        >
          ({selection.length})
        </Button>
      )}
      {selection.length > 0 && onShowCollectionModal && (
        <Button
          type="button"
          onClick={onShowCollectionModal}
          variant="secondary"
          size="sm"
          title="Add selected models to a collection"
          iconLeft={<FolderPlusIcon className="h-4 w-4" />}
        >
          Add to collection ({selection.length})
        </Button>
      )}
    </>
  );

  return (
    <>
      <FileBrowser
        ref={fileBrowserRef}
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        config={config as any}
        sortOptions={sortOptions}
        columns={modelColumns}
        renderItemActions={renderActions}
        extraToolbarActions={extraToolbarActions}
        viewMode={viewMode}
        onViewModeChange={onViewModeChange}
      />
      <ModelUploadModal
        isOpen={showUploadModal}
        onClose={() => setShowUploadModal(false)}
        onUploadSuccess={() => fileBrowserRef.current?.refetch() ?? Promise.resolve()}
      />
      {showPrintablesBrowserModal && (
        <PrintablesBrowserModal
          isOpen={showPrintablesBrowserModal}
          onClose={() => setShowPrintablesBrowserModal(false)}
          onImportUrl={(url) => {
            setSelectedPrintablesUrl(url);
            setShowPrintablesBrowserModal(false);
            setShowPrintablesImportModal(true);
          }}
        />
      )}
      <PrintablesImportModal
        isOpen={showPrintablesImportModal}
        initialUrl={selectedPrintablesUrl}
        onClose={() => {
          setShowPrintablesImportModal(false);
          setSelectedPrintablesUrl(null);
        }}
      />
      <ConfirmationModal
        isOpen={deleteConfirm.isOpen}
        title="Delete Model"
        message={deleteConfirm.file ? `Are you sure you want to delete "${deleteConfirm.file.fileName}"?` : ''}
        confirmButtonText="Delete"
        cancelButtonText="Cancel"
        isDangerous
        onConfirm={handleDeleteConfirm}
        onCancel={handleDeleteCancel}
      />
    </>
  );
};
