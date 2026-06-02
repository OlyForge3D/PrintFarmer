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
import { PrintablesImportModal } from '@/features/models3d/components/PrintablesImportModal';
import { Button } from '@/common/components/ui';
import { PrintablesIcon } from '@/common/components/icons/PrintablesIcon';
import { TagIcon, UploadIcon, EyeIcon, LayersTripleOutlineIcon, FilterIcon, DownloadIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
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
            <span
              key={tag.id}
              className="rounded-full bg-pf-bg-2 px-2 py-0.5 text-xs"
              style={tag.color ? { borderColor: tag.color, color: tag.color } : undefined}
            >
              {tag.name}
            </span>
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
}: ModelsFileBrowserProps) => {
  const { hasPermission } = useAuth();
  const [showUploadModal, setShowUploadModal] = useState(false);
  const [showPrintablesModal, setShowPrintablesModal] = useState(false);
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

  const handleDownload = useCallback((file: FileItem) => {
    if (file.isDirectory) return;
    const model3dFile = file.meta?.model3d as { name?: string } | undefined;
    const originalName = model3dFile?.name || file.fileName;
    const downloadUrl = `/api/3d-models/file/${file.id}`;
    
    // Create a link with the original filename for download
    const link = document.createElement('a');
    link.href = downloadUrl;
    link.download = originalName || '3d-model';
    link.style.display = 'none';
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
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
    (query: FileQueryState) => ({
      query: query.search || undefined,
      tagIds: selectedTags.length ? selectedTags : undefined,
      page: query.page,
      pageSize: query.pageSize,
      sortBy: query.sortBy === 'name' ? 'name' : query.sortBy === 'size' ? 'size' : 'uploadedAt',
      descending: query.sortOrder === 'desc',
    }),
    [selectedTags]
  );

  const fetcher = useCallback(
    async (params: unknown) => {
      const payload = params as ReturnType<typeof mapQueryParams>;
      const response = await apiClient.get3DModelsQuery(payload);

      const searchResponse = response as unknown as Model3DSearchResponse;

      // Guard against stub/malformed responses (e.g. slicer module disabled returns [])
      const models = Array.isArray(searchResponse?.models) ? searchResponse.models : [];
      const totalSize = models.reduce((sum: number, model: Model) => sum + (model.fileSize || 0), 0);

      return {
        items: models,
        totalItems: searchResponse?.totalCount ?? 0,
        totalPages: searchResponse?.totalPages ?? 0,
        totalSize,
        page: searchResponse?.page ?? 1,
      };
    },
    []
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
  )

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
          onClick={() => setShowPrintablesModal(true)}
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
    </>
  )

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
      <PrintablesImportModal
        isOpen={showPrintablesModal}
        onClose={() => setShowPrintablesModal(false)}
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
