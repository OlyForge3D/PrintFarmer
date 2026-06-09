import React, { Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { useNavigate, useLocation, useSearchParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { toast } from 'sonner';
import { PageTemplate } from '@/common/components/PageTemplate';
import { FileBrowser, type FileBrowserHandle } from '@/features/fileBrowser/components/FileBrowser';
import {
  type ColumnDef,
  type FileItem,
  type FileQueryState,
  type UseFileBrowserConfig,
} from '@/features/fileBrowser/types';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { usePrinters } from '@/common/hooks/useApi';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';
import { ViewerSkeleton } from '@/features/models3d/components/3d/ViewerSkeleton';
import { GcodeFileCard } from '@/features/gcode/components/GcodeFileCard';
import { PrintablesImportModal } from '@/features/models3d/components/PrintablesImportModal';
import { QuickSliceModal } from '@/features/slicer/components/QuickSliceModal';
import { HarvestWizardModal } from '@/features/gcode/components/harvest/HarvestWizardModal';
import { QueueGcodeModal } from '@/features/gcode/components/QueueGcodeModal';
import { AddToProjectModal } from '@/features/projects/components/AddToProjectModal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { ModelUploadModal } from '@/common/components/modals/ModelUploadModal';
import { GcodeUploadModal } from '@/common/components/modals/GcodeUploadModal';
import { BulkTagAssignmentModal } from '@/common/components/modals/BulkTagAssignmentModal';
import { TaggingModal } from '@/components/TaggingModal';
import { apiClient } from '@/services/api';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import {
  ActivityIcon,
  ClipboardListIcon,
  CubeIcon,
  DeleteIcon,
  DownloadIcon,
  EyeIcon,
  FileIcon,
  LayersTripleOutlineIcon,
  PlayIcon,
  TagIcon,
  UploadIcon,
} from '@/common/components/icons/MdiIcons';
import { PrintablesIcon } from '@/common/components/icons/PrintablesIcon';
import { Badge, Button, Checkbox } from '@/common/components/ui';
import type { Model, Model3DSearchResponse } from '@/types/models';
import type { GcodeFile, GetGcodeFilesResponse } from '@/types/api';
import type { ModelViewerProps } from '@/features/models3d/components/3d/ModelViewer3D';

const ModelViewer = lazyWithPreload<ModelViewerProps, React.FC<ModelViewerProps>>(
  () => import('@/features/models3d/components/3d/ModelViewer3D').then((module) => ({ default: module.ModelViewer }))
);
type FileTypeFilter = 'all' | 'models' | 'gcode' | 'other';
type FileSource = 'model' | 'gcode';
type SortOption = 'name' | 'size' | 'date';

type TagSummary = { id: string; name: string; color?: string };

interface UnifiedFileRecord {
  source: FileSource;
  actualId: string;
  path: string;
  displayName: string;
  fileSize?: number;
  uploadedAt?: string;
  thumbnailUrl?: string;
  tags?: TagSummary[];
  extension: string;
  filter: Exclude<FileTypeFilter, 'all'>;
  model?: Model;
  gcode?: GcodeFile;
}

interface TaggingTarget {
  actualId: string;
  objectType: 'Model3D' | 'GcodeFile';
  name: string;
  tags: TagSummary[];
}

interface UnifiedQueryParams {
  page: number;
  pageSize: number;
  search: string;
  sortBy: SortOption;
  sortOrder: 'asc' | 'desc';
  filter: FileTypeFilter;
}

const FILE_TYPE_OPTIONS: Array<{ value: FileTypeFilter; label: string; hint: string }> = [
  { value: 'all', label: 'All', hint: 'Everything in one list' },
  { value: 'models', label: '3D Models', hint: '.3mf, .stl, .step' },
  { value: 'gcode', label: 'G-Code', hint: '.gcode' },
  { value: 'other', label: 'Other', hint: 'OBJ, PLY, and uncategorized files' },
];
const LEGACY_SEGMENT_TO_FILTER: Partial<Record<string, FileTypeFilter>> = {
  models: 'models',
  '3d-models': 'models',
  gcode: 'gcode',
};
const LEGACY_ACTION_SEGMENTS = new Set(['harvest']);
const MODEL_FILE_EXTENSIONS = new Set(['3mf', 'stl', 'step', 'stp']);
const GCODE_FILE_EXTENSIONS = new Set(['gcode', 'gco', 'g', 'ngc', 'gc']);
const DEFAULT_FETCH_PAGE_SIZE = 100;
const MAX_FETCH_PAGES = 10; // Cap at 1000 files per source to prevent runaway requests
const FILE_BROWSER_SORT_OPTIONS: Array<{ value: SortOption; label: string }> = [
  { value: 'date', label: 'Date' },
  { value: 'name', label: 'Name' },
  { value: 'size', label: 'Size' },
];

function getNormalizedExtension(name: string | null | undefined, fallback?: string): string {
  if (!name) {
    return fallback?.replace(/^\./, '').trim().toLowerCase() || '';
  }

  const rawExtension = name.split('.').pop() ?? fallback ?? '';
  return rawExtension.replace(/^\./, '').trim().toLowerCase();
}

function normalizeUploadedAt(value: Date | string | undefined): string | undefined {
  if (!value) {
    return undefined;
  }

  const normalizedDate = new Date(value);
  return Number.isNaN(normalizedDate.getTime()) ? undefined : normalizedDate.toISOString();
}

function classifyModel(model: Model): Exclude<FileTypeFilter, 'all'> {
  return MODEL_FILE_EXTENSIONS.has(getNormalizedExtension(model.name, model.fileType)) ? 'models' : 'other';
}

function toUnifiedModel(model: Model): UnifiedFileRecord {
  return {
    source: 'model',
    actualId: model.id ?? '',
    path: model.path ?? '',
    displayName: model.name ?? 'Untitled',
    fileSize: model.fileSize,
    uploadedAt: normalizeUploadedAt(model.uploadedAt),
    thumbnailUrl: model.thumbnailUrl,
    tags: model.tags?.map((tag) => ({ id: tag.id, name: tag.name, color: tag.color })),
    extension: getNormalizedExtension(model.name, model.fileType),
    filter: classifyModel(model),
    model,
  };
}

function toUnifiedGcode(file: GcodeFile): UnifiedFileRecord {
  return {
    source: 'gcode',
    actualId: file.id ?? '',
    path: file.path ?? '',
    displayName: file.name ?? 'Untitled',
    fileSize: file.fileSize,
    uploadedAt: normalizeUploadedAt(file.uploadedAt),
    thumbnailUrl: file.thumbnailUrl,
    tags: file.tags?.map((tag) => ({ id: tag.id, name: tag.name, color: tag.color })),
    extension: getNormalizedExtension(file.name),
    filter: GCODE_FILE_EXTENSIONS.has(getNormalizedExtension(file.name)) ? 'gcode' : 'other',
    gcode: file,
  };
}

function compareFiles(a: UnifiedFileRecord, b: UnifiedFileRecord, sortBy: SortOption, sortOrder: 'asc' | 'desc') {
  const comparison = sortBy === 'size'
    ? (a.fileSize ?? 0) - (b.fileSize ?? 0)
    : sortBy === 'date'
      ? new Date(a.uploadedAt ?? 0).getTime() - new Date(b.uploadedAt ?? 0).getTime()
      : (a.displayName ?? '').localeCompare(b.displayName ?? '', undefined, { sensitivity: 'base' });

  const fallbackComparison = comparison === 0
    ? (a.displayName ?? '').localeCompare(b.displayName ?? '', undefined, { sensitivity: 'base' })
    : comparison;

  return sortOrder === 'asc' ? fallbackComparison : fallbackComparison * -1;
}

function getFilterValueFromSearchParams(searchParams: URLSearchParams): FileTypeFilter {
  const requested = searchParams.get('type');
  return FILE_TYPE_OPTIONS.some((option) => option.value === requested)
    ? (requested as FileTypeFilter)
    : 'all';
}

function toPrefixedId(source: FileSource, actualId: string): string {
  return `${source}:${actualId}`;
}

function fromPrefixedId(value: string): { source: FileSource; actualId: string } | null {
  const separatorIndex = value.indexOf(':');
  if (separatorIndex <= 0) {
    return null;
  }

  const source = value.slice(0, separatorIndex);
  if (source !== 'model' && source !== 'gcode') {
    return null;
  }

  return {
    source,
    actualId: value.slice(separatorIndex + 1),
  };
}

function toFileItem(file: UnifiedFileRecord): FileItem {
  return {
    id: toPrefixedId(file.source, file.actualId),
    path: file.path,
    fileName: file.displayName,
    isDirectory: false,
    fileSize: file.fileSize,
    uploadedAt: file.uploadedAt,
    tags: file.tags,
    thumbnailUrl: file.thumbnailUrl,
    meta: {
      ...(file.gcode ? { gcode: file.gcode } : {}),
      ...(file.model ? { model3d: file.model as unknown as Record<string, unknown> } : {}),
    },
  };
}

function getFileExtension(file: FileItem): string {
  const model = file.meta?.model3d as Model | undefined;
  return getNormalizedExtension(file.fileName, model?.fileType).toUpperCase();
}

function getSourceLabel(file: FileItem): string {
  return file.meta?.gcode ? 'G-Code' : '3D Model';
}

function formatBytes(bytes?: number) {
  if (!bytes) {
    return '—';
  }

  const kilo = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const sizeIndex = Math.floor(Math.log(bytes) / Math.log(kilo));
  return `${(bytes / Math.pow(kilo, sizeIndex)).toFixed(1)} ${sizes[sizeIndex]}`;
}

function buildModelCard(
  file: FileItem,
  isSelected: boolean,
  onToggle: () => void,
  onOpenModel: (model: Model) => void,
  onSliceModel: (model: Model) => void,
  onDeleteModel: (file: FileItem) => void,
  onTagItem: (target: TaggingTarget) => void,
  onDownloadModel: (file: FileItem) => void
) {
  const model = file.meta?.model3d as Model | undefined;
  if (!model) {
    return null;
  }

  const filterCategory = classifyModel(model);
  const categoryLabel = filterCategory === 'other' ? 'Other' : '3D Model';

  return (
    <div
      className={clsx(
        'group overflow-hidden rounded-lg border bg-pf-bg-0 transition-all focus-within:ring-2 focus-within:ring-pf-accent',
        isSelected ? 'border-pf-accent shadow-md' : 'border-pf-border hover:border-pf-accent/50 hover:shadow-md'
      )}
    >
      <div className="relative aspect-square overflow-hidden bg-pf-bg-1">
        {model.thumbnailUrl ? (
          <img
            src={model.thumbnailUrl}
            alt={model.name}
            className="h-full w-full object-contain p-2"
          />
        ) : (
          <div className="flex h-full items-center justify-center text-pf-text-secondary">
            <CubeIcon className="h-12 w-12 opacity-50" />
          </div>
        )}
        <div className="absolute left-2 top-2">
          <Checkbox
            aria-label={`Select ${file.fileName}`}
            checked={isSelected}
            onChange={onToggle}
          />
        </div>
        <div className="absolute right-2 top-2 flex gap-1">
          <Badge variant="primary">{categoryLabel}</Badge>
          <Badge variant="default">{getFileExtension(file)}</Badge>
        </div>
      </div>

      <div className="flex flex-col gap-3 p-3">
        <div className="min-w-0">
          <h3 className="line-clamp-2 text-sm font-semibold text-pf-text-primary">{model.name}</h3>
          <p className="mt-1 text-xs text-pf-text-secondary">{getSourceLabel(file)}</p>
        </div>

        <div className="space-y-1.5 border-t border-pf-border pt-2 text-xs">
          <div className="flex items-center justify-between gap-2">
            <span className="text-pf-text-secondary">Size</span>
            <span className="font-medium text-pf-text-primary">{formatBytes(file.fileSize)}</span>
          </div>
          <div className="flex items-center justify-between gap-2">
            <span className="text-pf-text-secondary">Uploaded</span>
            <span className="font-medium text-pf-text-primary">
              {file.uploadedAt ? new Date(file.uploadedAt).toLocaleDateString() : '—'}
            </span>
          </div>
          {model.tags && model.tags.length > 0 && (
            <div className="flex flex-wrap gap-1 pt-1">
              {model.tags.slice(0, 3).map((tag) => (
                <span
                  key={tag.id}
                  className="rounded-full bg-pf-bg-2 px-2 py-0.5 text-[10px] font-medium"
                  style={tag.color ? { color: tag.color } : undefined}
                >
                  {tag.name}
                </span>
              ))}
            </div>
          )}
        </div>

        <div className="flex flex-wrap gap-2 border-t border-pf-border pt-2">
          <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={() => onDownloadModel(file)}
            title="Download file"
          >
            <DownloadIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={() => onTagItem({ actualId: model.id, objectType: 'Model3D', name: model.name, tags: model.tags ?? [] })}
            title="Tag file"
          >
            <TagIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={() => onOpenModel(model)}
            title="Preview file"
          >
            <EyeIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="sm"
            variant="primary"
            onClick={() => onSliceModel(model)}
            title="Open quick slice"
          >
            <LayersTripleOutlineIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            className="text-pf-error hover:bg-pf-error/10 hover:text-pf-error"
            onClick={() => onDeleteModel(file)}
            title="Delete file"
          >
            <DeleteIcon className="h-4 w-4" />
          </Button>
        </div>
      </div>
    </div>
  );
}

async function fetchAllModels(search: string, sortBy: SortOption, sortOrder: 'asc' | 'desc', signal?: AbortSignal) {
  const request = {
    query: search || undefined,
    page: 1,
    pageSize: DEFAULT_FETCH_PAGE_SIZE,
    sortBy: sortBy === 'size' ? 'size' : sortBy === 'name' ? 'name' : 'uploadedAt',
    descending: sortOrder === 'desc',
  };

  if (signal?.aborted) throw new Error('Query was cancelled');

  const firstPage = (await apiClient.get3DModelsQuery(request)) as unknown as Model3DSearchResponse;
  const totalPages = Math.min(firstPage.totalPages ?? 1, MAX_FETCH_PAGES);

  if (signal?.aborted) throw new Error('Query was cancelled');

  const remainingPages = totalPages > 1
    ? await Promise.all(
        Array.from({ length: totalPages - 1 }, (_, index) =>
          apiClient.get3DModelsQuery({ ...request, page: index + 2 }) as Promise<unknown>
        )
      )
    : [];

  if (signal?.aborted) throw new Error('Query was cancelled');

  return [
    ...(firstPage.models ?? []),
    ...remainingPages.flatMap((page) => ((page as Model3DSearchResponse).models ?? [])),
  ];
}

async function fetchAllGcode(
  search: string,
  sortBy: SortOption,
  sortOrder: 'asc' | 'desc',
  harvestId?: string,
  printerId?: string,
  signal?: AbortSignal
) {
  const request = {
    search: search || undefined,
    harvestId,
    printerId,
    page: 1,
    pageSize: DEFAULT_FETCH_PAGE_SIZE,
    sortBy,
    sortOrder,
  };

  if (signal?.aborted) throw new Error('Query was cancelled');

  const firstPage = await apiClient.getGcodeFilesQuery(request as never) as GetGcodeFilesResponse;
  const totalPages = Math.min(firstPage.totalPages ?? 1, MAX_FETCH_PAGES);

  if (signal?.aborted) throw new Error('Query was cancelled');

  const remainingPages = totalPages > 1
    ? await Promise.all(
        Array.from({ length: totalPages - 1 }, (_, index) =>
          apiClient.getGcodeFilesQuery({ ...request, page: index + 2 } as never)
        )
      )
    : [];

  if (signal?.aborted) throw new Error('Query was cancelled');

  return [
    ...(firstPage.files ?? []),
    ...remainingPages.flatMap((page) => page.files ?? []),
  ].filter((file) => !file.isDirectory);
}

export function FilesPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const [searchParams] = useSearchParams();
  const { viewMode, setViewMode } = useViewModePreference('printfarmer-files-viewmode');
  const { hasPermission } = useAuth();
  const { data: printers = [] } = usePrinters({ staleTime: 30_000 });
  const { data: harvestOperations = [] } = useQuery({
    queryKey: ['files-harvest-operations'],
    queryFn: () => apiClient.getHarvestOperations(),
    refetchInterval: hasPermission('gcode_harvest', 'execute') ? 5_000 : false,
    enabled: hasPermission('gcode_harvest', 'execute'),
  });

  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [showModelUploadModal, setShowModelUploadModal] = useState(false);
  const [showGcodeUploadModal, setShowGcodeUploadModal] = useState(false);
  const [showPrintablesModal, setShowPrintablesModal] = useState(false);
  const [showHarvestModal, setShowHarvestModal] = useState(false);
  const [showBulkTagModal, setShowBulkTagModal] = useState(false);
  const [showAddToProjectModal, setShowAddToProjectModal] = useState(false);
  const [showBulkDeleteConfirm, setShowBulkDeleteConfirm] = useState(false);
  const [taggingTarget, setTaggingTarget] = useState<TaggingTarget | null>(null);
  const [viewerModel, setViewerModel] = useState<Model | null>(null);
  const [queueFile, setQueueFile] = useState<GcodeFile | null>(null);
  const [quickSliceModel, setQuickSliceModel] = useState<Model | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<FileItem | null>(null);
  const [deleteMode, setDeleteMode] = useState<'single' | 'bulk'>('single');
  const [isDeleting, setIsDeleting] = useState(false);
  const fileBrowserRef = useRef<FileBrowserHandle>(null);

  const selectedFilter = getFilterValueFromSearchParams(searchParams);
  const harvestId = searchParams.get('harvest') ?? undefined;
  const printerId = searchParams.get('printer') ?? undefined;

  useEffect(() => {
    const legacySegment = location.pathname.replace(/^\/files\/?/, '').split('/')[0];

    // Legacy /files/harvest → open harvest modal at /files
    if (LEGACY_ACTION_SEGMENTS.has(legacySegment)) {
      const frame = window.requestAnimationFrame(() => setShowHarvestModal(true));
      navigate('/files', { replace: true });
      return () => window.cancelAnimationFrame(frame);
    }

    const normalizedFilter = LEGACY_SEGMENT_TO_FILTER[legacySegment];
    const tabFilter = LEGACY_SEGMENT_TO_FILTER[searchParams.get('tab') ?? ''];
    const replacementFilter = normalizedFilter ?? tabFilter;

    if (!replacementFilter) {
      return;
    }

    const nextParams = new URLSearchParams(searchParams);
    nextParams.delete('tab');
    nextParams.set('type', replacementFilter);
    navigate(`/files?${nextParams.toString()}`, { replace: true });
  }, [location.pathname, navigate, searchParams]);

  useEffect(() => {
    const frame = window.requestAnimationFrame(() => {
      setSelectedIds((current) => (current.length === 0 ? current : []));
    });

    return () => window.cancelAnimationFrame(frame);
  }, [selectedFilter]);

  const selectedModelIds = useMemo(
    () => selectedIds.flatMap((id) => {
      const parsed = fromPrefixedId(id);
      return parsed?.source === 'model' ? [parsed.actualId] : [];
    }),
    [selectedIds]
  );
  const selectedGcodeIds = useMemo(
    () => selectedIds.flatMap((id) => {
      const parsed = fromPrefixedId(id);
      return parsed?.source === 'gcode' ? [parsed.actualId] : [];
    }),
    [selectedIds]
  );

  const handleFilterChange = useCallback((filter: FileTypeFilter) => {
    const nextParams = new URLSearchParams(searchParams);
    if (filter === 'all') {
      nextParams.delete('type');
    } else {
      nextParams.set('type', filter);
    }
    navigate(nextParams.toString() ? `/files?${nextParams.toString()}` : '/files', { replace: true });
  }, [navigate, searchParams]);

  const handleRefresh = useCallback(async () => {
    await fileBrowserRef.current?.refetch();
  }, []);

  const handleDownloadModel = useCallback((file: FileItem) => {
    const model = file.meta?.model3d as Model | undefined;
    if (!model) {
      return;
    }

    const link = document.createElement('a');
    link.href = `${getApiBaseUrl()}/3d-models/file/${model.id}`;
    link.download = model.name;
    link.style.display = 'none';
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
  }, []);

  const handleDownloadGcode = useCallback((file: FileItem) => {
    const gcode = file.meta?.gcode as GcodeFile | undefined;
    if (!gcode) {
      return;
    }

    const link = document.createElement('a');
    link.href = `${getApiBaseUrl()}/gcode-files/file/${gcode.id}`;
    link.download = gcode.name;
    link.style.display = 'none';
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
  }, []);

  const fetcher = useCallback(async (params: unknown, signal?: AbortSignal) => {
    const query = params as UnifiedQueryParams;
    // 'other' can contain uncategorized files from either source, so fetch both
    const shouldFetchModels = query.filter !== 'gcode';
    const shouldFetchGcode = query.filter === 'all' || query.filter === 'gcode' || query.filter === 'other';

    const [models, gcodeFiles] = await Promise.all([
      shouldFetchModels ? fetchAllModels(query.search, query.sortBy, query.sortOrder, signal) : Promise.resolve([]),
      shouldFetchGcode ? fetchAllGcode(query.search, query.sortBy, query.sortOrder, harvestId, printerId, signal) : Promise.resolve([]),
    ]);

    const mergedFiles = [
      ...models.filter((model): model is Model => model != null).map(toUnifiedModel),
      ...gcodeFiles.filter((file): file is GcodeFile => file != null).map(toUnifiedGcode),
    ].filter((file) => query.filter === 'all' || file.filter === query.filter);

    mergedFiles.sort((left, right) => compareFiles(left, right, query.sortBy, query.sortOrder));

    const totalItems = mergedFiles.length;
    const totalPages = Math.max(1, Math.ceil(totalItems / query.pageSize));
    const page = Math.min(query.page, totalPages);
    const startIndex = (page - 1) * query.pageSize;
    const pagedFiles = mergedFiles.slice(startIndex, startIndex + query.pageSize);

    return {
      items: pagedFiles,
      totalItems,
      totalPages,
      totalSize: mergedFiles.reduce((sum, file) => sum + (file.fileSize ?? 0), 0),
      page,
      folders: [],
      currentPath: '/',
    };
  }, [harvestId, printerId]);

  const config: UseFileBrowserConfig<UnifiedFileRecord> = useMemo(() => ({
    fetcher,
    mapDomainToFileItem: toFileItem,
    mapQueryParams: (query: FileQueryState): UnifiedQueryParams => ({
      page: query.page,
      pageSize: query.pageSize,
      search: query.search,
      sortBy: (query.sortBy === 'size' || query.sortBy === 'date' ? query.sortBy : 'name') as SortOption,
      sortOrder: query.sortOrder,
      filter: selectedFilter,
    }),
    defaultSort: { sortBy: 'date', sortOrder: 'desc' },
    selectedIds,
    onSelectionChange: setSelectedIds,
    initialPath: '/',
    viewMode,
    onViewModeChange: (mode) => setViewMode(mode),
  }), [fetcher, selectedFilter, selectedIds, setViewMode, viewMode]);

  const modelColumns = useMemo<ColumnDef[]>(() => [
    { key: 'fileName', label: 'Name', sortable: true },
    {
      key: 'category',
      label: 'Type',
      render: (file) => (
        <span className="text-xs font-medium uppercase text-pf-text-secondary">
          {getFileExtension(file)}
        </span>
      ),
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
      key: 'details',
      label: 'Details',
      render: (file) => {
        const gcode = file.meta?.gcode as GcodeFile | undefined;
        const model = file.meta?.model3d as Model | undefined;

        if (gcode) {
          return gcode.extractedPrinterModelName || gcode.requiredMaterial || gcode.extractedMaterial || '—';
        }

        if (model?.tags?.length) {
          return `${model.tags.length} tag${model.tags.length === 1 ? '' : 's'}`;
        }

        return model?.fileType?.toUpperCase() ?? '—';
      },
    },
  ], []);

  const renderItemActions = useCallback((file: FileItem) => {
    const gcode = file.meta?.gcode as GcodeFile | undefined;
    const model = file.meta?.model3d as Model | undefined;

    if (gcode) {
      return (
        <>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={() => setQueueFile(gcode)}
            title="Queue for printing"
          >
            <PlayIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={() => setTaggingTarget({ actualId: gcode.id, objectType: 'GcodeFile', name: gcode.name, tags: gcode.tags ?? [] })}
            title="Tag file"
          >
            <TagIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={() => handleDownloadGcode(file)}
            title="Download file"
          >
            <DownloadIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={() => {
              setSelectedIds([file.id]);
              setShowAddToProjectModal(true);
            }}
            title="Add to project"
          >
            <ClipboardListIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            className="text-pf-error hover:bg-pf-error/10 hover:text-pf-error"
            onClick={() => {
              setDeleteTarget(file);
              setDeleteMode('single');
            }}
            title="Delete file"
          >
            <DeleteIcon className="h-4 w-4" />
          </Button>
        </>
      );
    }

    if (!model) {
      return null;
    }

    return (
      <>
        <Button
          type="button"
          size="sm"
          variant="secondary"
          onClick={() => handleDownloadModel(file)}
          title="Download file"
        >
          <DownloadIcon className="h-4 w-4" />
        </Button>
        <Button
          type="button"
          size="sm"
          variant="secondary"
          onClick={() => setTaggingTarget({ actualId: model.id, objectType: 'Model3D', name: model.name, tags: model.tags ?? [] })}
          title="Tag file"
        >
          <TagIcon className="h-4 w-4" />
        </Button>
        <Button
          type="button"
          size="sm"
          variant="secondary"
          onClick={() => setViewerModel(model)}
          title="Preview file"
        >
          <EyeIcon className="h-4 w-4" />
        </Button>
        <Button
          type="button"
          size="sm"
          variant="primary"
          onClick={() => setQuickSliceModel(model)}
          title="Quick slice"
        >
          <LayersTripleOutlineIcon className="h-4 w-4" />
        </Button>
        <Button
          type="button"
          size="sm"
          variant="secondary"
          className="text-pf-error hover:bg-pf-error/10 hover:text-pf-error"
          onClick={() => {
            setDeleteTarget(file);
            setDeleteMode('single');
          }}
          title="Delete file"
        >
          <DeleteIcon className="h-4 w-4" />
        </Button>
      </>
    );
  }, [handleDownloadGcode, handleDownloadModel]);

  const renderMetadata = useCallback((file: FileItem) => {
    const gcode = file.meta?.gcode as GcodeFile | undefined;
    const model = file.meta?.model3d as Model | undefined;

    if (gcode) {
      return (
        <>
          {(gcode.requiredMaterial || gcode.extractedMaterial) && (
            <div className="flex items-center justify-between gap-2">
              <span className="text-pf-text-secondary">Material:</span>
              <span className="font-medium text-pf-text-primary line-clamp-1">{gcode.requiredMaterial || gcode.extractedMaterial}</span>
            </div>
          )}
          {gcode.extractedPrinterModelName && (
            <div className="flex items-center justify-between gap-2">
              <span className="text-pf-text-secondary">Printer:</span>
              <span className="font-medium text-pf-text-primary line-clamp-1">{gcode.extractedPrinterModelName}</span>
            </div>
          )}
        </>
      );
    }

    if (model?.tags?.length) {
      return (
        <div className="flex items-center justify-between gap-2">
          <span className="text-pf-text-secondary">Tags:</span>
          <span className="font-medium text-pf-text-primary">{model.tags.length}</span>
        </div>
      );
    }

    return (
      <div className="flex items-center justify-between gap-2">
        <span className="text-pf-text-secondary">Source:</span>
        <span className="font-medium text-pf-text-primary">{getSourceLabel(file)}</span>
      </div>
    );
  }, []);

  const renderCard = useCallback((file: FileItem, isSelected: boolean, onToggle: () => void) => {
    const gcode = file.meta?.gcode as GcodeFile | undefined;
    if (gcode) {
      return (
        <div className={clsx('relative h-full rounded-lg transition-all', isSelected && 'ring-2 ring-pf-accent')}>
          <div className="absolute left-2 top-2 z-10">
            <Checkbox
              aria-label={`Select ${file.fileName}`}
              checked={isSelected}
              onChange={onToggle}
            />
          </div>
          <GcodeFileCard
            file={gcode}
            onDownload={() => handleDownloadGcode(file)}
            onDelete={() => {
              setDeleteTarget(file);
              setDeleteMode('single');
            }}
          />
        </div>
      );
    }

    return buildModelCard(
      file,
      isSelected,
      onToggle,
      setViewerModel,
      setQuickSliceModel,
      (selectedFile) => {
        setDeleteTarget(selectedFile);
        setDeleteMode('single');
      },
      setTaggingTarget,
      handleDownloadModel
    );
  }, [handleDownloadGcode, handleDownloadModel]);

  const handleDeleteFiles = useCallback(async (idsToDelete: string[]) => {
    if (!idsToDelete.length) {
      return;
    }

    setIsDeleting(true);
    let successCount = 0;
    let failedCount = 0;

    for (const id of idsToDelete) {
      const parsed = fromPrefixedId(id);
      if (!parsed) {
        failedCount++;
        continue;
      }

      try {
        if (parsed.source === 'model') {
          await apiClient.deleteModel3dFile(parsed.actualId);
        } else {
          await apiClient.deleteGcodeFile(parsed.actualId);
        }
        successCount++;
      } catch {
        failedCount++;
      }
    }

    setIsDeleting(false);
    setDeleteTarget(null);
    setShowBulkDeleteConfirm(false);
    setSelectedIds([]);
    await handleRefresh();

    if (successCount > 0 && failedCount === 0) {
      toast.success(`Deleted ${successCount} file${successCount === 1 ? '' : 's'}`);
      return;
    }

    if (successCount > 0) {
      toast.warning(`Deleted ${successCount} file${successCount === 1 ? '' : 's'}, ${failedCount} failed`);
      return;
    }

    toast.error('Failed to delete selected files');
  }, [handleRefresh]);

  const headerActions = (
    <div className="flex flex-wrap items-center gap-2">
      {hasPermission('3d_models', 'create') && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          iconLeft={<PrintablesIcon />}
          onClick={() => setShowPrintablesModal(true)}
        >
          Printables
        </Button>
      )}
      {hasPermission('3d_models', 'create') && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          iconLeft={<CubeIcon className="h-4 w-4" />}
          onClick={() => setShowModelUploadModal(true)}
        >
          Upload Model
        </Button>
      )}
      {hasPermission('gcode_harvest', 'create') && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          iconLeft={<UploadIcon className="h-4 w-4" />}
          onClick={() => setShowGcodeUploadModal(true)}
        >
          Upload G-Code
        </Button>
      )}
      {hasPermission('gcode_harvest', 'execute') && (
        <Button
          type="button"
          variant="primary"
          size="sm"
          iconLeft={<ActivityIcon className="h-4 w-4" />}
          onClick={() => setShowHarvestModal(true)}
        >
          Start Harvest
        </Button>
      )}
    </div>
  );

  const toolbarActions = (
    <>
      {selectedModelIds.length > 0 && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          iconLeft={<TagIcon className="h-4 w-4" />}
          onClick={() => setShowBulkTagModal(true)}
          title={`Tag ${selectedModelIds.length} selected model${selectedModelIds.length === 1 ? '' : 's'}`}
        >
          Tag Models ({selectedModelIds.length})
        </Button>
      )}
      {selectedGcodeIds.length > 0 && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          iconLeft={<ClipboardListIcon className="h-4 w-4" />}
          onClick={() => setShowAddToProjectModal(true)}
          title={`Add ${selectedGcodeIds.length} selected file${selectedGcodeIds.length === 1 ? '' : 's'} to a project`}
        >
          Add to Project ({selectedGcodeIds.length})
        </Button>
      )}
      {selectedIds.length > 0 && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          className="text-pf-error hover:bg-pf-error/10 hover:text-pf-error"
          iconLeft={<DeleteIcon className="h-4 w-4" />}
          onClick={() => {
            setDeleteMode('bulk');
            setShowBulkDeleteConfirm(true);
          }}
          title={`Delete ${selectedIds.length} selected file${selectedIds.length === 1 ? '' : 's'}`}
        >
          Delete ({selectedIds.length})
        </Button>
      )}
    </>
  );

  const filterButtons = (
    <>
      {FILE_TYPE_OPTIONS.map((option) => {
        const isActive = selectedFilter === option.value;
        return (
          <Button
            key={option.value}
            type="button"
            size="sm"
            variant={isActive ? 'primary' : 'secondary'}
            aria-pressed={isActive}
            onClick={() => handleFilterChange(option.value)}
            title={option.hint}
          >
            {option.label}
          </Button>
        );
      })}
    </>
  );

  return (
    <>
      <PageTemplate
        title="Files"
        subtitle="Browse models and print artifacts together, then narrow the list by file type when you need focus."
        icon={FileIcon}
        actions={headerActions}
      >
        <div className="min-h-[65vh] min-w-0 overflow-hidden">
          <FileBrowser
            ref={fileBrowserRef}
            config={config}
            sortOptions={FILE_BROWSER_SORT_OPTIONS}
            columns={modelColumns}
            renderItemActions={renderItemActions}
            renderMetadata={renderMetadata}
            renderCard={renderCard}
            filterActions={filterButtons}
            extraToolbarActions={toolbarActions}
            viewMode={viewMode}
            onViewModeChange={setViewMode}
          />
        </div>
      </PageTemplate>

      <ModelUploadModal
        isOpen={showModelUploadModal}
        onClose={() => setShowModelUploadModal(false)}
        onUploadSuccess={async () => handleRefresh()}
      />
      <GcodeUploadModal
        isOpen={showGcodeUploadModal}
        onClose={() => setShowGcodeUploadModal(false)}
        onFilesSelected={async (files, onProgress, onItemComplete) => {
          for (const file of files) {
            try {
              await apiClient.uploadGcodeLibraryFile(file, '/', onProgress);
              onItemComplete?.(file.name, 'done');
            } catch (error) {
              const message = error instanceof Error ? error.message : 'Upload failed';
              onItemComplete?.(file.name, 'error', message);
            }
          }
          await handleRefresh();
        }}
      />
      <PrintablesImportModal
        isOpen={showPrintablesModal}
        onClose={() => {
          setShowPrintablesModal(false);
          void handleRefresh();
        }}
      />
      <HarvestWizardModal
        isOpen={showHarvestModal}
        onClose={() => setShowHarvestModal(false)}
        printers={printers}
        activeHarvests={harvestOperations}
        onComplete={() => {
          toast.success('Harvest completed');
          handleRefresh();
        }}
      />
      <BulkTagAssignmentModal
        isOpen={showBulkTagModal}
        onClose={() => {
          setShowBulkTagModal(false);
          void handleRefresh();
        }}
        initialSelectedModelIds={selectedModelIds}
      />
      <AddToProjectModal
        fileIds={selectedGcodeIds}
        isOpen={showAddToProjectModal}
        onClose={() => setShowAddToProjectModal(false)}
      />
      {taggingTarget && (
        <TaggingModal
          isOpen={Boolean(taggingTarget)}
          onClose={() => {
            setTaggingTarget(null);
            void handleRefresh();
          }}
          objectId={taggingTarget.actualId}
          objectType={taggingTarget.objectType}
          initialTags={taggingTarget.tags}
        />
      )}
      <QueueGcodeModal
        file={queueFile}
        isOpen={queueFile !== null}
        onClose={() => setQueueFile(null)}
      />
      <QuickSliceModal
        isOpen={quickSliceModel !== null}
        onClose={() => setQuickSliceModel(null)}
        model={quickSliceModel}
      />
      <ConfirmationModal
        isOpen={deleteMode === 'single' && deleteTarget !== null}
        title="Delete File"
        message={deleteTarget ? `Are you sure you want to delete "${deleteTarget.fileName}"?` : ''}
        confirmButtonText="Delete"
        cancelButtonText="Cancel"
        isDangerous
        isConfirming={isDeleting}
        onConfirm={() => handleDeleteFiles(deleteTarget ? [deleteTarget.id] : [])}
        onCancel={() => setDeleteTarget(null)}
      />
      <ConfirmationModal
        isOpen={deleteMode === 'bulk' && showBulkDeleteConfirm}
        title="Delete Selected Files"
        message={`Delete ${selectedIds.length} selected file${selectedIds.length === 1 ? '' : 's'}? This cannot be undone.`}
        confirmButtonText="Delete"
        cancelButtonText="Cancel"
        isDangerous
        isConfirming={isDeleting}
        onConfirm={() => handleDeleteFiles(selectedIds)}
        onCancel={() => setShowBulkDeleteConfirm(false)}
      />

      {viewerModel && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
          role="dialog"
          aria-modal="true"
          aria-label={`3D preview of ${viewerModel.name}`}
          onKeyDown={(e) => { if (e.key === 'Escape') setViewerModel(null); }}
          onClick={(e) => { if (e.target === e.currentTarget) setViewerModel(null); }}
        >
          <div className="flex max-h-[90vh] w-full max-w-4xl flex-col rounded-lg border border-pf-border bg-pf-bg-1 shadow-xl">
            <div className="flex shrink-0 items-center justify-between border-b border-pf-border p-4">
              <h3 className="text-lg font-medium text-pf-text-primary">{viewerModel.name}</h3>
              <Button onClick={() => setViewerModel(null)} variant="subtle" size="sm" aria-label="Close preview">
                Close
              </Button>
            </div>
            <div className="flex-1 overflow-y-auto p-4">
              <Suspense fallback={<ViewerSkeleton variant="model" className="h-128 w-full" />}>
                {(viewerModel.url || viewerModel.id) && viewerModel.fileType && (
                  <ModelViewer
                    modelUrl={viewerModel.url || `${getApiBaseUrl()}/3d-models/file/${viewerModel.id}`}
                    fileType={viewerModel.fileType}
                    showGrid={true}
                    className="h-128 w-full"
                  />
                )}
              </Suspense>
            </div>
          </div>
        </div>
      )}

    </>
  );
}
