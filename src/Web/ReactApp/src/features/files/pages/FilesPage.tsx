import React, { Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useLocation, useSearchParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { toast } from 'sonner';
import { PageTemplate } from '@/common/components/PageTemplate';
import { LazyModalBoundary } from '@/common/components/LazyModalFallback';
import { FileBrowser, type FileBrowserHandle } from '@/features/fileBrowser/components/FileBrowser';
import {
  type ColumnDef,
  type FileItem,
  type FileQueryState,
  type UseFileBrowserConfig,
} from '@/features/fileBrowser/types';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';
import { ViewerSkeleton } from '@/features/models3d/components/3d/ViewerSkeleton';
import { GcodeFileCard } from '@/features/gcode/components/GcodeFileCard';
import { AddToProjectModal } from '@/features/projects/components/AddToProjectModal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { Modal } from '@/common/components/modals/Modal';
import { PrinterFileCatalog } from '@/features/files/components/PrinterFileCatalog';
import { CompactFileCard } from '@/features/files/components/CompactFileCard';
import { FileActionIconButtons, type FileAction } from '@/features/files/components/FileActions';
import { FileDate } from '@/features/files/components/FileDate';
import { FileTypeGlyph } from '@/features/files/components/FileGlyphs';
import { FileNameButton } from '@/features/files/components/FileNameButton';
import { FileThumbnail } from '@/features/files/components/FileThumbnail';
import { LocalFileDetails } from '@/features/files/components/LocalFileDetails';
import { LocalLibraryFolders } from '@/features/files/components/LocalLibraryFolders';
import { useFileDetailSelection } from '@/features/files/hooks/useFileDetailSelection';
import {
  fileExtension,
  fileKindFromName,
  formatSizeLabel,
  type FileKind,
} from '@/features/files/utils/fileDisplay';
import { ModelUploadModal } from '@/common/components/modals/ModelUploadModal';
import { GcodeUploadModal } from '@/common/components/modals/GcodeUploadModal';
import { BulkTagAssignmentModal } from '@/common/components/modals/BulkTagAssignmentModal';
import { TaggingModal } from '@/components/TaggingModal';
import { apiClient } from '@/services/api';
import { deleteLocalLibraryFiles, describeLocalLibraryDelete } from '@/features/files/utils/localLibraryBulkDelete';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import {
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
import { Button, FormField, Select, Tabs } from '@/common/components/ui';
import type { Model } from '@/types/models';
import type { GcodeFile, UnifiedFile } from '@/types/api';
import type { ModelViewerProps } from '@/features/models3d/components/3d/ModelViewer3D';

const ModelViewer = lazyWithPreload<ModelViewerProps, React.FC<ModelViewerProps>>(
  () => import('@/features/models3d/components/3d/ModelViewer3D').then((module) => ({ default: module.ModelViewer }))
);
type FileTypeFilter = 'all' | 'models' | 'gcode' | 'other';
type FileSource = 'model' | 'gcode';
type PrintablesImportModalComponent = typeof import('@/features/models3d/components/PrintablesImportModal')['PrintablesImportModal'];
type PrintablesBrowserModalComponent = typeof import('@/features/models3d/components/PrintablesBrowserModal')['PrintablesBrowserModal'];
type QuickSliceModalComponent = typeof import('@/features/slicer/components/QuickSliceModal')['QuickSliceModal'];
type QueueGcodeModalComponent = typeof import('@/features/gcode/components/QueueGcodeModal')['QueueGcodeModal'];

const PrintablesImportModal = lazyWithPreload<React.ComponentProps<PrintablesImportModalComponent>, PrintablesImportModalComponent>(
  () => import('@/features/models3d/components/PrintablesImportModal').then((module) => ({ default: module.PrintablesImportModal })),
);
const PrintablesBrowserModal = lazyWithPreload<React.ComponentProps<PrintablesBrowserModalComponent>, PrintablesBrowserModalComponent>(
  () => import('@/features/models3d/components/PrintablesBrowserModal').then((module) => ({ default: module.PrintablesBrowserModal })),
);
const QuickSliceModal = lazyWithPreload<React.ComponentProps<QuickSliceModalComponent>, QuickSliceModalComponent>(
  () => import('@/features/slicer/components/QuickSliceModal').then((module) => ({ default: module.QuickSliceModal })),
);
const QueueGcodeModal = lazyWithPreload<React.ComponentProps<QueueGcodeModalComponent>, QueueGcodeModalComponent>(
  () => import('@/features/gcode/components/QueueGcodeModal').then((module) => ({ default: module.QueueGcodeModal })),
);
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
  path: string;
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
  { value: 'gcode', label: 'G-Code', hint: '.gcode, .bgcode' },
  { value: 'other', label: 'Other', hint: 'OBJ, PLY, and uncategorized files' },
];
const LEGACY_SEGMENT_TO_FILTER: Partial<Record<string, FileTypeFilter>> = {
  models: 'models',
  '3d-models': 'models',
  gcode: 'gcode',
};
const MODEL_FILE_EXTENSIONS = new Set(['3mf', 'stl', 'step', 'stp']);
const GCODE_FILE_EXTENSIONS = new Set(['gcode', 'bgcode', 'gco', 'g', 'ngc', 'gc']);
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
  return MODEL_FILE_EXTENSIONS.has(getNormalizedExtension(undefined, model.fileType)) ? 'models' : 'other';
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
  const extension = getNormalizedExtension(file.fileName, file.fileType);
  return {
    source: 'gcode',
    actualId: file.id ?? '',
    path: file.path ?? '',
    displayName: file.name ?? 'Untitled',
    fileSize: file.fileSize,
    uploadedAt: normalizeUploadedAt(file.uploadedAt),
    thumbnailUrl: file.thumbnailUrl,
    tags: file.tags?.map((tag) => ({ id: tag.id, name: tag.name, color: tag.color })),
    extension,
    filter: GCODE_FILE_EXTENSIONS.has(extension) ? 'gcode' : 'other',
    gcode: file,
  };
}

function isModelFileType(fileType: string): fileType is Model['fileType'] {
  return fileType === 'stl'
    || fileType === '3mf'
    || fileType === 'obj'
    || fileType === 'ply'
    || fileType === 'step'
    || fileType === 'stp';
}

function toUnifiedFile(file: UnifiedFile): UnifiedFileRecord {
  const tags = file.tags?.map((tag) => ({
    id: tag.id,
    name: tag.name,
    color: tag.color,
  }));

  if (file.source === 'Model') {
    const fileType = isModelFileType(file.fileType)
      ? file.fileType
      : getNormalizedExtension(file.name, file.fileType);
    const model: Model = {
      id: file.id,
      path: file.path,
      name: file.name,
      fileName: file.fileName,
      fileSize: file.fileSize,
      fileType: isModelFileType(fileType) ? fileType : 'stl',
      uploadedAt: file.uploadedAt,
      url: file.url,
      thumbnailUrl: file.thumbnailUrl,
      tags,
    };
    return toUnifiedModel(model);
  }

  const gcode: GcodeFile = {
    id: file.id,
    path: file.path,
    fileName: file.fileName,
    name: file.name,
    fileType: file.fileType,
    fileSize: file.fileSize,
    uploadedAt: new Date(file.uploadedAt),
    isDirectory: false,
    thumbnailUrl: file.thumbnailUrl,
    tags: file.tags,
    requiredMaterial: file.requiredMaterial,
    extractedSlicerName: file.extractedSlicerName,
    extractedSlicerVersion: file.extractedSlicerVersion,
    extractedPrintTime: file.extractedPrintTime,
    extractedFilamentLength: file.extractedFilamentLength,
    extractedNozzleDiameter: file.extractedNozzleDiameter,
    extractedMaterial: file.extractedMaterial,
    extractedPrinterModel: file.extractedPrinterModel,
    extractedPrinterModelName: file.extractedPrinterModelName,
    extractedLayerHeight: file.extractedLayerHeight,
    extractedInfill: file.extractedInfill,
    extractedPerimeters: file.extractedPerimeters,
    extractedHotendTemp: file.extractedHotendTemp,
    extractedBedTemp: file.extractedBedTemp,
  };
  return toUnifiedGcode(gcode);
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
  const gcode = file.meta?.gcode as GcodeFile | undefined;
  // Display names may omit the extension; fall back to the stored file type.
  const extension = fileExtension(file.fileName)
    || getNormalizedExtension(undefined, model?.fileType ?? gcode?.fileType);
  return extension.toUpperCase();
}

function getFileKind(file: FileItem): FileKind {
  if (file.meta?.gcode) return 'gcode';
  if (file.meta?.model3d) return 'model';
  return fileKindFromName(file.fileName);
}
export function FilesPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const [searchParams] = useSearchParams();
  const { viewMode, setViewMode } = useViewModePreference('printfarmer-files-viewmode');
  const { hasPermission } = useAuth();
  const onPrinters = searchParams.get('source') === 'printers';
  const linkedFileId = onPrinters ? null : searchParams.get('file');
  const linkedFile = useQuery({
    queryKey: ['gcode-files', linkedFileId],
    queryFn: () => apiClient.getGcodeFile(linkedFileId!),
    enabled: !!linkedFileId,
  });

  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [showModelUploadModal, setShowModelUploadModal] = useState(false);
  const [showGcodeUploadModal, setShowGcodeUploadModal] = useState(false);
  const printablesButtonRef = useRef<HTMLButtonElement>(null);
  const [showPrintablesBrowserModal, setShowPrintablesBrowserModal] = useState(false);
  const [showPrintablesImportModal, setShowPrintablesImportModal] = useState(false);
  const [selectedPrintablesUrl, setSelectedPrintablesUrl] = useState<string | null>(null);
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
  const printerId = searchParams.get('printer') ?? undefined;

  useEffect(() => {
    const legacySegment = location.pathname.replace(/^\/files\/?/, '').split('/')[0];

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

  const handleDownloadModel = useCallback(async (file: FileItem) => {
    const model = file.meta?.model3d as Model | undefined;
    if (!model) {
      return;
    }

    try {
      await apiClient.downloadModel3dFile(model.id, model.name || model.fileName);
    } catch {
      toast.error('Failed to download model');
    }
  }, []);

  const handleDownloadGcode = useCallback(async (file: FileItem) => {
    const gcode = file.meta?.gcode as GcodeFile | undefined;
    if (!gcode) {
      return;
    }

    try {
      await apiClient.downloadGcodeFileById(gcode.id, gcode.name);
    } catch {
      toast.error('Failed to download G-code file');
    }
  }, []);

  const fetcher = useCallback(async (params: unknown, signal?: AbortSignal) => {
    const query = params as UnifiedQueryParams;
    const response = await apiClient.getUnifiedFiles({
      page: query.page,
      pageSize: query.pageSize,
      search: query.search || undefined,
      path: query.path || undefined,
      sortBy: query.sortBy,
      sortOrder: query.sortOrder,
      filter: query.filter,
      printerId,
    }, signal);

    return {
      items: response.items.map(toUnifiedFile),
      totalItems: response.totalItems,
      totalPages: response.totalPages,
      totalSize: response.totalSize,
      page: response.page,
      folders: [],
      currentPath: query.path,
    };
  }, [printerId]);

  const config: UseFileBrowserConfig<UnifiedFileRecord> = useMemo(() => ({
    fetcher,
    mapDomainToFileItem: toFileItem,
    mapQueryParams: (query: FileQueryState): UnifiedQueryParams => ({
      path: query.path,
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
    initialPath: '',
    viewMode,
    onViewModeChange: (mode) => setViewMode(mode),
  }), [fetcher, selectedFilter, selectedIds, setViewMode, viewMode]);

  const details = useFileDetailSelection<FileItem>();
  const openDetails = details.open;
  const closeDetails = details.close;
  const activeDetailId = details.selected?.id;

  // One action list per file feeds the card/row icon buttons and the labelled panel buttons.
  const localActions = useCallback((file: FileItem): FileAction[] => {
    const gcode = file.meta?.gcode as GcodeFile | undefined;
    const model = file.meta?.model3d as Model | undefined;
    const deleteAction: FileAction = {
      id: 'delete',
      label: 'Delete file',
      icon: <DeleteIcon className="h-4 w-4" />,
      tone: 'danger',
      onClick: () => {
        setDeleteTarget(file);
        setDeleteMode('single');
      },
    };

    if (gcode) {
      return [
        {
          id: 'queue',
          label: 'Queue for printing',
          icon: <PlayIcon className="h-4 w-4" />,
          onClick: () => setQueueFile(gcode),
          onPreload: () => void QueueGcodeModal.preload(),
        },
        {
          id: 'tag',
          label: 'Tag file',
          icon: <TagIcon className="h-4 w-4" />,
          onClick: () => setTaggingTarget({ actualId: gcode.id, objectType: 'GcodeFile', name: gcode.name, tags: gcode.tags ?? [] }),
        },
        {
          id: 'download',
          label: 'Download file',
          icon: <DownloadIcon className="h-4 w-4" />,
          onClick: () => void handleDownloadGcode(file),
        },
        {
          id: 'project',
          label: 'Add to project',
          icon: <ClipboardListIcon className="h-4 w-4" />,
          onClick: () => {
            setSelectedIds([file.id]);
            setShowAddToProjectModal(true);
          },
        },
        deleteAction,
      ];
    }

    if (!model) {
      return [];
    }

    return [
      {
        id: 'download',
        label: 'Download file',
        icon: <DownloadIcon className="h-4 w-4" />,
        onClick: () => void handleDownloadModel(file),
      },
      {
        id: 'tag',
        label: 'Tag file',
        icon: <TagIcon className="h-4 w-4" />,
        onClick: () => setTaggingTarget({ actualId: model.id, objectType: 'Model3D', name: model.name, tags: model.tags ?? [] }),
      },
      {
        id: 'preview',
        label: 'Preview file',
        icon: <EyeIcon className="h-4 w-4" />,
        onClick: () => setViewerModel(model),
      },
      {
        id: 'quick-slice',
        label: 'Quick slice',
        icon: <LayersTripleOutlineIcon className="h-4 w-4" />,
        tone: 'primary',
        onClick: () => setQuickSliceModel(model),
        onPreload: () => void QuickSliceModal.preload(),
      },
      deleteAction,
    ];
  }, [handleDownloadGcode, handleDownloadModel]);

  // The thumbnail sits between selection and name; type and extended metadata live in details.
  const modelColumns = useMemo<ColumnDef[]>(() => [
    {
      key: 'thumbnail',
      label: 'Preview',
      render: (file) => <FileThumbnail kind={getFileKind(file)} size="row" src={file.thumbnailUrl} />,
    },
    {
      key: 'fileName',
      label: 'Name',
      sortable: true,
      render: (file) => (
        <FileNameButton name={file.fileName} className="max-w-xs" onOpen={(invoker) => openDetails(file, invoker)} />
      ),
    },
    {
      key: 'fileSize',
      label: 'Size',
      sortable: true,
      align: 'right',
      render: (file) => formatSizeLabel(file.fileSize),
    },
    {
      key: 'uploadedAt',
      label: 'Uploaded',
      sortable: true,
      render: (file) => <FileDate value={file.uploadedAt} fallback="—" />,
    },
  ], [openDetails]);

  const renderItemActions = useCallback(
    (file: FileItem) => <FileActionIconButtons actions={localActions(file)} dense />,
    [localActions],
  );

  const renderCard = useCallback((file: FileItem, isSelected: boolean, onToggle: () => void) => {
    const kind = getFileKind(file);
    return (
      <CompactFileCard
        name={file.fileName}
        thumbnail={<FileThumbnail kind={kind} size="card" src={file.thumbnailUrl} />}
        meta={formatSizeLabel(file.fileSize)}
        status={<FileTypeGlyph kind={kind} className="h-3.5 w-3.5" />}
        actions={localActions(file)}
        selection={{ checked: isSelected, onToggle }}
        isActive={activeDetailId === file.id}
        onOpen={(invoker) => openDetails(file, invoker)}
      />
    );
  }, [activeDetailId, localActions, openDetails]);
  const handleDeleteFiles = useCallback(async (idsToDelete: string[]) => {
    if (!idsToDelete.length) {
      return;
    }

    setIsDeleting(true);
    try {
      const summary = await deleteLocalLibraryFiles(idsToDelete, apiClient, fromPrefixedId);
      setDeleteTarget(null);
      setShowBulkDeleteConfirm(false);
      // Blocked, failed and unknown items stay selected so the user can review or retry them.
      setSelectedIds((current) => current.filter((id) => !summary.removedIds.has(id)));
      if (activeDetailId && summary.removedIds.has(activeDetailId)) {
        closeDetails();
      }
      const { level, message } = describeLocalLibraryDelete(summary);
      toast[level](message);
    } finally {
      setIsDeleting(false);
      await handleRefresh();
    }
  }, [activeDetailId, closeDetails, handleRefresh]);

  const headerActions = (
    <div className="flex flex-wrap items-center gap-2">
      {hasPermission('3d_models', 'create') && (
        <Button
          ref={printablesButtonRef}
          type="button"
          variant="secondary"
          size="sm"
          iconLeft={<PrintablesIcon />}
          onClick={() => setShowPrintablesBrowserModal(true)}
          onFocus={() => void PrintablesBrowserModal.preload()}
          onPointerEnter={() => void PrintablesBrowserModal.preload()}
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
      {hasPermission('gcode_library', 'create') && (
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

  const fileTypeFilter = (
    <FormField label="Type" htmlFor="local-library-type">
      <Select id="local-library-type" value={selectedFilter} className="min-w-32"
        onChange={event => {
          const option = FILE_TYPE_OPTIONS.find(option => option.value === event.target.value);
          if (option) handleFilterChange(option.value);
        }}>
        {FILE_TYPE_OPTIONS.map(option => (
          <option key={option.value} value={option.value} title={option.hint}>{option.label}</option>
        ))}
      </Select>
    </FormField>
  );

  return (
    <>
      <PageTemplate
        title="Files"
        subtitle="Browse owned local assets or metadata observations on printers. Remote files become local only when you explicitly copy or queue."
        icon={FileIcon}
        actions={headerActions}
      >
        <Tabs activeTab={onPrinters ? 'printers' : 'local'} onTabChange={source => {
          const next = new URLSearchParams(searchParams);
          if (source === 'printers') next.set('source', 'printers');
          else next.delete('source');
          next.delete('printer');
          next.delete('file');
          navigate(`/files${next.size ? `?${next.toString()}` : ''}`);
          setSelectedIds([]);
          closeDetails();
        }}>
          <Tabs.List aria-label="File source">
            <Tabs.Tab id="local">Local library</Tabs.Tab>
            <Tabs.Tab id="printers">On printers</Tabs.Tab>
          </Tabs.List>
          {/* overflow-clip keeps the docked detail panel sticky while clipping overflow. */}
          <Tabs.Panels className="min-h-[65vh] min-w-0 overflow-clip">
            <Tabs.Panel id="printers"><PrinterFileCatalog /></Tabs.Panel>
            <Tabs.Panel id="local">
              <div className="flex items-start gap-4">
                <div className="min-w-0 flex-1">
                  <FileBrowser
                    ref={fileBrowserRef}
                    config={config}
                    sortOptions={FILE_BROWSER_SORT_OPTIONS}
                    columns={modelColumns}
                    renderItemActions={renderItemActions}
                    renderCard={renderCard}
                    filterActions={fileTypeFilter}
                    extraToolbarActions={toolbarActions}
                    renderFolderNavigation={(path, onNavigate, controls) => (
                      <LocalLibraryFolders
                        controls={controls}
                        path={path}
                        onNavigate={(nextPath) => { closeDetails(); onNavigate(nextPath); }}
                        modelIds={selectedModelIds}
                        gcodeIds={selectedGcodeIds}
                        canCreate={hasPermission('3d_models', 'create') && hasPermission('gcode_library', 'create')}
                        canMove={(selectedModelIds.length === 0 || hasPermission('3d_models', 'create'))
                          && (selectedGcodeIds.length === 0 || hasPermission('gcode_library', 'create'))}
                        onMoved={() => { setSelectedIds([]); closeDetails(); }}
                      />
                    )}
                    viewMode={viewMode}
                    onViewModeChange={setViewMode}
                    compact
                  />
                </div>
                {details.selected && (
                  <LocalFileDetails
                    key={details.selected.id}
                    file={details.selected}
                    kind={getFileKind(details.selected)}
                    extension={getFileExtension(details.selected)}
                    actions={localActions(details.selected)}
                    onClose={closeDetails}
                  />
                )}
              </div>
            </Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      </PageTemplate>

      <Modal isOpen={!!linkedFileId} title="Owned local G-code" onClose={() => {
        const next = new URLSearchParams(searchParams);
        next.delete('file');
        navigate(`/files${next.size ? `?${next.toString()}` : ''}`, { replace: true });
      }}>
        {linkedFile.isLoading && <p role="status">Loading local asset metadata...</p>}
        {linkedFile.error && <p role="alert" className="text-pf-error">Failed to load local asset: {linkedFile.error.message}</p>}
        {linkedFile.data && <GcodeFileCard file={linkedFile.data} onDownload={async () => {
          try {
            await apiClient.downloadGcodeFileById(linkedFile.data.id, linkedFile.data.name);
          } catch (error) {
            toast.error(`Failed to download: ${error instanceof Error ? error.message : 'Unknown error'}`);
          }
        }} />}
      </Modal>

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
      {showPrintablesBrowserModal && (
        <LazyModalBoundary
          label="printables browser"
          onCancel={() => setShowPrintablesBrowserModal(false)}
          onRetry={PrintablesBrowserModal.retry}
          restoreFocus={() => printablesButtonRef.current?.focus()}
        >
          <PrintablesBrowserModal
            isOpen={showPrintablesBrowserModal}
            onClose={() => setShowPrintablesBrowserModal(false)}
            onImportUrl={(url) => {
              void PrintablesImportModal.preload();
              setSelectedPrintablesUrl(url);
              setShowPrintablesBrowserModal(false);
              setShowPrintablesImportModal(true);
            }}
          />
        </LazyModalBoundary>
      )}
      {showPrintablesImportModal && (
        <LazyModalBoundary
          label="printables import"
          onCancel={() => {
            setShowPrintablesImportModal(false);
            setSelectedPrintablesUrl(null);
          }}
          onRetry={PrintablesImportModal.retry}
          restoreFocus={() => printablesButtonRef.current?.focus()}
        >
          <PrintablesImportModal
            isOpen={showPrintablesImportModal}
            initialUrl={selectedPrintablesUrl}
            onClose={() => {
              setShowPrintablesImportModal(false);
              setSelectedPrintablesUrl(null);
              void handleRefresh();
            }}
          />
        </LazyModalBoundary>
      )}
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
      {queueFile && (
        <LazyModalBoundary
          label="print queue"
          onCancel={() => setQueueFile(null)}
          onRetry={QueueGcodeModal.retry}
        >
          <QueueGcodeModal
            file={queueFile}
            isOpen
            onClose={() => setQueueFile(null)}
          />
        </LazyModalBoundary>
      )}
      {quickSliceModel && (
        <LazyModalBoundary
          label="quick slice"
          onCancel={() => setQuickSliceModel(null)}
          onRetry={QuickSliceModal.retry}
        >
          <QuickSliceModal
            isOpen
            onClose={() => setQuickSliceModel(null)}
            model={quickSliceModel}
          />
        </LazyModalBoundary>
      )}
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
