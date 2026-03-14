import { useCallback, useMemo, useState } from 'react';
import { toast } from 'sonner';
import { useQueryClient } from '@tanstack/react-query';
import { FileBrowser } from '@/features/fileBrowser/components/FileBrowser';
import { type ColumnDef, type FileItem, type FileQueryState, type GcodeFileItem, type UseFileBrowserConfig } from '@/features/fileBrowser/types';
import { apiClient } from '@/services/api';
import { Button, Checkbox } from '@/common/components/ui';
import { UploadIcon, DownloadIcon, DeleteIcon, TagIcon, FilterIcon, PlayIcon, NozzleIcon, BedIcon, ClipboardListIcon } from '@/common/components/icons/MdiIcons';
import { GcodeUploadModal } from '@/common/components/modals/GcodeUploadModal';
import { QueueGcodeModal } from '@/features/gcode/components/QueueGcodeModal';
import { GcodeFileCard } from '@/features/gcode/components/GcodeFileCard';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { signalRService } from '@/services/harvest-signalr';
import type { GcodeFile, GetGcodeFilesResponse } from '@/types/api';

const toGcodeFileItem = (file: GcodeFile): GcodeFileItem => ({
  id: file.id,
  path: file.path,
  fileName: file.name,
  isDirectory: file.isDirectory,
  fileSize: file.fileSize,
  uploadedAt: file.uploadedAt?.toString(),
  tags: file.tags?.map((tag) => ({ id: tag.id, name: tag.name, color: tag.color })),
  thumbnailUrl: file.thumbnailUrl,
  meta: {
    gcode: file as unknown as Record<string, unknown>,
  },
});

const formatBytes = (bytes?: number) => {
  if (!bytes) return '—';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${(bytes / Math.pow(k, i)).toFixed(1)} ${sizes[i]}`;
};

const gcodeColumns: ColumnDef[] = [
  { key: 'fileName', label: 'Name', sortable: true },
  {
    key: 'fileSize',
    label: 'Size',
    sortable: true,
    align: 'right',
    render: (file) => formatBytes(file.fileSize),
  },
  {
    key: 'uploadedAt',
    label: 'Modified',
    sortable: true,
    render: (file) => (file.uploadedAt ? new Date(file.uploadedAt).toLocaleString() : '—'),
  },
  {
    key: 'thumbnailUrl',
    label: 'Thumbnail',
    render: (file) =>
      file.thumbnailUrl ? (
        <img
          src={file.thumbnailUrl}
          alt={file.fileName}
          className="h-12 w-12 rounded-sm object-cover"
        />
      ) : (
        '—'
      ),
  },
  {
    key: 'requiredMaterial',
    label: 'Material',
    sortable: true,
    render: (file) => {
      const gcodeMeta = file.meta?.gcode;
      return gcodeMeta?.requiredMaterial || gcodeMeta?.extractedMaterial || '—';
    },
  },
  {
    key: 'extractedNozzleDiameter',
    label: 'Nozzle',
    sortable: true,
    align: 'right',
    render: (file) => {
      const nozzle = file.meta?.gcode?.extractedNozzleDiameter;
      return nozzle ? `${nozzle}mm` : '—';
    },
  },
  {
    key: 'extractedHotendTemp',
    label: 'Hotend',
    align: 'right',
    render: (file) => {
      const temp = file.meta?.gcode?.extractedHotendTemp;
      return temp ? (
        <span className="flex items-center justify-end gap-1">
          <NozzleIcon className="w-3.5 h-3.5 text-pf-error" isOn={false} />
          {Math.round(temp)}°C
        </span>
      ) : '—';
    },
  },
  {
    key: 'extractedBedTemp',
    label: 'Bed',
    align: 'right',
    render: (file) => {
      const temp = file.meta?.gcode?.extractedBedTemp;
      return temp ? (
        <span className="flex items-center justify-end gap-1">
          <BedIcon className="w-3.5 h-3.5 text-pf-accent" isOn={false} />
          {Math.round(temp)}°C
        </span>
      ) : '—';
    },
  },
  {
    key: 'extractedPrinterModelName',
    label: 'Printer Model',
    sortable: true,
    render: (file) => file.meta?.gcode?.extractedPrinterModelName || '—',
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

interface GcodeFileBrowserProps {
  harvestId?: string;
  printerId?: string;
  viewMode?: 'grid' | 'explorer';
  onViewModeChange?: (mode: 'grid' | 'explorer') => void;
  isModal?: boolean;
  selectedTags?: string[];
  selectedPrinterModels?: string[];
  selectedFileIds?: string[];
  onSelectionChange?: (ids: string[]) => void;
  onShowTagModal?: () => void;
  onShowSingleTagModal?: (file: GcodeFile) => void;
  onShowAddToProjectModal?: () => void;
  onToggleTagFilterPanel?: () => void;
  onAvailablePrinterModelsChange?: (models: Array<{ id: string | null; name: string }>) => void;
}

export const GcodeFileBrowser = ({
  harvestId,
  printerId,
  viewMode,
  onViewModeChange,
  selectedTags = [],
  selectedPrinterModels = [],
  selectedFileIds,
  onSelectionChange,
  onShowTagModal,
  onShowSingleTagModal,
  onShowAddToProjectModal,
  onToggleTagFilterPanel,
  onAvailablePrinterModelsChange,
}: GcodeFileBrowserProps) => {
  const { hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const [showUploadModal, setShowUploadModal] = useState(false);
  const [localSelection, setLocalSelection] = useState<string[]>([]);
  
  // Only use local state if viewMode prop is not provided (uncontrolled mode)
  const [localViewMode, setLocalViewMode] = useState<'grid' | 'explorer'>('grid');

  // Queue modal state
  const [queueFileToAdd, setQueueFileToAdd] = useState<GcodeFile | null>(null);
  const [showQueueModal, setShowQueueModal] = useState(false);

  const selection = selectedFileIds ?? localSelection;

  const handleSelectionChange = useCallback(
    (ids: string[]) => {
      if (!selectedFileIds) {
        setLocalSelection(ids);
      }
      onSelectionChange?.(ids);
    },
    [onSelectionChange, selectedFileIds]
  );

  const handleDownload = useCallback((file: FileItem) => {
    if (file.isDirectory) return;
    const gcodeFile = file.meta?.gcode as GcodeFile | undefined;
    const originalName = gcodeFile?.name || file.fileName;
    const downloadUrl = `${getApiBaseUrl()}/gcode-files/file/${file.id}`;
    
    // Create a link with the original filename for download
    const link = document.createElement('a');
    link.href = downloadUrl;
    link.download = originalName || 'gcode-file';
    link.style.display = 'none';
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
  }, []);

  const [deleteConfirm, setDeleteConfirm] = useState<{ isOpen: boolean; file: FileItem | null }>({
    isOpen: false,
    file: null,
  });

  // Bulk delete state
  const [bulkDeleteConfirm, setBulkDeleteConfirm] = useState(false);

  const handleDeleteClick = useCallback((file: FileItem) => {
    setDeleteConfirm({ isOpen: true, file });
  }, []);

  const handleDeleteConfirm = useCallback(async () => {
    if (!deleteConfirm.file) return;
    const file = deleteConfirm.file;
    setDeleteConfirm({ isOpen: false, file: null });
    
    try {
      await apiClient.deleteGcodeFile(file.id);
      toast.success('File deleted');
      // Invalidate the cache to refresh the file list
      queryClient.invalidateQueries({ queryKey: ['file-browser'] });
      // Also clear selection if the deleted file was selected
      if (selection.includes(file.id)) {
        handleSelectionChange(selection.filter(id => id !== file.id));
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to delete file';
      toast.error('Failed to delete file');
      console.error('Delete error:', message);
    }
  }, [deleteConfirm.file, queryClient, selection, handleSelectionChange]);

  const handleDeleteCancel = useCallback(() => {
    setDeleteConfirm({ isOpen: false, file: null });
  }, []);

  // Bulk delete handlers
  const handleBulkDeleteClick = useCallback(() => {
    if (selection.length > 0) {
      setBulkDeleteConfirm(true);
    }
  }, [selection.length]);

  const handleBulkDeleteConfirm = useCallback(async () => {
    setBulkDeleteConfirm(false);
    const idsToDelete = [...selection];
    
    let successCount = 0;
    let failCount = 0;
    
    for (const id of idsToDelete) {
      try {
        await apiClient.deleteGcodeFile(id);
        successCount++;
      } catch (error) {
        console.error(`Failed to delete file ${id}:`, error);
        failCount++;
      }
    }
    
    // Clear selection and refresh
    handleSelectionChange([]);
    queryClient.invalidateQueries({ queryKey: ['file-browser'] });
    
    // Show result toast
    if (successCount > 0 && failCount === 0) {
      toast.success(`Deleted ${successCount} file${successCount > 1 ? 's' : ''}`);
    } else if (successCount > 0 && failCount > 0) {
      toast.warning(`Deleted ${successCount} file${successCount > 1 ? 's' : ''}, ${failCount} failed`);
    } else {
      toast.error(`Failed to delete ${failCount} file${failCount > 1 ? 's' : ''}`);
    }
  }, [selection, handleSelectionChange, queryClient]);

  const handleBulkDeleteCancel = useCallback(() => {
    setBulkDeleteConfirm(false);
  }, []);

  const renderActions = useCallback(
    (file: FileItem) => {
      if (file.isDirectory) return null;

      const gcodeFile = file.meta?.gcode as GcodeFile | undefined;
      if (!gcodeFile) return null;

      return (
        <>
          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={() => {
              setQueueFileToAdd(gcodeFile);
              setShowQueueModal(true);
            }}
            title="Queue for printing"
          >
            <PlayIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={() => onShowSingleTagModal?.(gcodeFile)}
            title="Tag file"
          >
            <TagIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={() => handleDownload(file)}
            title="Download file"
          >
            <DownloadIcon className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            variant="secondary"
            size="sm"
            className="text-pf-error hover:text-pf-error hover:bg-pf-error/10"
            onClick={() => handleDeleteClick(file)}
            title="Delete file"
          >
            <DeleteIcon className="h-4 w-4" />
          </Button>
        </>
      );
    },
    [handleDownload, handleDeleteClick, onShowSingleTagModal]
  );

  const renderMetadata = useCallback(
    (file: FileItem) => {
      const gcodeMeta = file.meta?.gcode as { requiredMaterial?: string; extractedMaterial?: string; extractedNozzleDiameter?: number; extractedPrinterModelName?: string; extractedHotendTemp?: number; extractedBedTemp?: number } | undefined;
      if (!gcodeMeta) return null;

      return (
        <>
          {(gcodeMeta.requiredMaterial || gcodeMeta.extractedMaterial) && (
            <div className="flex justify-between items-center gap-2">
              <span className="text-pf-text-secondary">Material:</span>
              <span className="text-pf-text-primary font-medium line-clamp-1">
                {gcodeMeta.requiredMaterial || gcodeMeta.extractedMaterial}
              </span>
            </div>
          )}
          {gcodeMeta.extractedNozzleDiameter && (
            <div className="flex justify-between items-center gap-2">
              <span className="text-pf-text-secondary">Nozzle:</span>
              <span className="text-pf-text-primary font-medium">{gcodeMeta.extractedNozzleDiameter}mm</span>
            </div>
          )}
          {(gcodeMeta.extractedHotendTemp || gcodeMeta.extractedBedTemp) && (
            <div className="flex justify-between items-center gap-2">
              <span className="text-pf-text-secondary">Temps:</span>
              <span className="text-pf-text-primary font-medium">
                {gcodeMeta.extractedHotendTemp ? `${Math.round(gcodeMeta.extractedHotendTemp)}°` : '—'}
                {' / '}
                {gcodeMeta.extractedBedTemp ? `${Math.round(gcodeMeta.extractedBedTemp)}°` : '—'}
              </span>
            </div>
          )}
          {gcodeMeta.extractedPrinterModelName && (
            <div className="flex justify-between items-center gap-2">
              <span className="text-pf-text-secondary">Printer:</span>
              <span className="text-pf-text-primary font-medium line-clamp-1">{gcodeMeta.extractedPrinterModelName}</span>
            </div>
          )}
        </>
      );
    },
    []
  );
  
  // Use prop if provided (controlled), otherwise use local state (uncontrolled)
  const currentViewMode = viewMode ?? localViewMode;

  const renderGcodeCard = useCallback(
    (file: FileItem, isSelected: boolean, onToggle: () => void) => {
      const gcodeFile = file.meta?.gcode as GcodeFile | undefined;
      if (!gcodeFile) return null;

      return (
        <div className={`relative h-full rounded-lg transition-all ${isSelected ? 'ring-2 ring-pf-primary' : ''}`}>
          <div className="absolute top-2 left-2 z-10">
            <Checkbox
              aria-label={`Select ${file.fileName}`}
              checked={isSelected}
              onChange={onToggle}
            />
          </div>
          <GcodeFileCard
            file={gcodeFile}
            onDownload={() => handleDownload(file)}
            onDelete={() => handleDeleteClick(file)}
          />
        </div>
      );
    },
    [handleDownload, handleDeleteClick],
  );
  
  // Handle view mode changes
  const handleViewModeChange = useCallback((mode: 'grid' | 'explorer') => {
    if (viewMode === undefined) {
      // Uncontrolled mode - update local state
      setLocalViewMode(mode);
    }
    // Always call parent callback if provided
    onViewModeChange?.(mode);
  }, [viewMode, onViewModeChange]);

  const sortOptions = useMemo(
    () => [
      { value: 'name', label: 'Name' },
      { value: 'size', label: 'Size' },
      { value: 'date', label: 'Date' },
    ],
    []
  );

  const fetcher = useCallback(
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    async (params: any, signal?: AbortSignal) => {
      // Extract viewMode from params to determine which endpoint to use
      const { viewMode: paramViewMode, ...apiParams } = params;
      const isExplorerMode = paramViewMode === 'explorer';
      
      // Debug logging with timestamp
      if (typeof window !== 'undefined' && (window as { PrintFarmerDebug?: { gcodeFileBrowser?: boolean } }).PrintFarmerDebug?.gcodeFileBrowser) {
        console.log('[GcodeFileBrowser] Fetcher called at', new Date().toISOString(), 'with:', {
          paramViewMode,
          isExplorerMode,
          path: apiParams.path,
          currentStateViewMode: currentViewMode,
          aborted: signal?.aborted,
          allParams: params
        });
      }
      
      // If already aborted, don't even start
      if (signal?.aborted) {
        throw new Error('Query was cancelled');
      }
      
      // Use new efficient query endpoint for both modes
      if (typeof window !== 'undefined' && (window as { PrintFarmerDebug?: { gcodeFileBrowser?: boolean } }).PrintFarmerDebug?.gcodeFileBrowser) {
        console.log('[GcodeFileBrowser] Calling /gcode-files/query', isExplorerMode ? '(explorer mode)' : '(grid mode)');
      }
      const response: GetGcodeFilesResponse = await apiClient.getGcodeFilesQuery(apiParams as never);
      
      // Extract availablePrinterModels from response and notify parent
      if (response.availablePrinterModels) {
        onAvailablePrinterModelsChange?.(response.availablePrinterModels);
      }
      
      // Log what we got back from API
      if (typeof window !== 'undefined' && (window as { PrintFarmerDebug?: { gcodeFileBrowser?: boolean } }).PrintFarmerDebug?.gcodeFileBrowser) {
        console.log('[GcodeFileBrowser] API response:', {
          filesCount: response.files?.length ?? 0,
          totalFiles: response.totalFiles,
          totalItems: response.totalItems,
          firstFile: response.files?.[0]
        });
      }

      // Build folder tree ONLY in explorer mode (grid view doesn't need folders)
      let folders: Array<{ path: string; name: string; children?: Array<{ path: string; name: string }> }> = [];
      if (isExplorerMode) {
        // Check if query was cancelled before making folders call
        if (signal?.aborted) {
          throw new Error('Query was cancelled before folders fetch');
        }
        
        if (typeof window !== 'undefined' && (window as { PrintFarmerDebug?: { gcodeFileBrowser?: boolean } }).PrintFarmerDebug?.gcodeFileBrowser) {
          console.log('[GcodeFileBrowser] Calling /gcode-files/folders (explorer mode)');
        }
        const folderEntries = await apiClient.getGcodeFilesFolders();
        
        // Create a map of all folders for quick lookup
        const folderMap = new Map<string, { path: string; name: string; children: Array<{ path: string; name: string }> }>();
        
        // Always include root folder
        folderMap.set('/', { path: '/', name: '/', children: [] });
        
        folderEntries.forEach((folder) => {
          if (!folderMap.has(folder.path)) {
            const folderName = folder.path === '/' ? '/' : folder.path.split('/').filter(Boolean).pop() || folder.path;
            folderMap.set(folder.path, { path: folder.path, name: folderName, children: [] });
          }
        });

        // Build parent-child relationships
        folderMap.forEach((folderNode, folderPath) => {
          if (folderPath === '/') return; // Skip root processing
          
          // Find parent folder by removing the last segment
          const parts = folderPath.split('/').filter(Boolean);
          const parentPath = parts.length === 1 ? '/' : '/' + parts.slice(0, -1).join('/');
          
          const parentNode = folderMap.get(parentPath);
          if (parentNode) {
            parentNode.children.push(folderNode);
          }
        });

        // Return only root folder (which has all subfolders nested under it)
        const rootFolder = folderMap.get('/');
        folders = rootFolder ? [rootFolder] : [];
      }

      return {
        items: response.files ?? [],
        totalItems: response.totalFiles ?? response.totalItems ?? 0,
        totalPages: response.totalPages ?? 1,
        totalSize: response.totalSize ?? 0,
        page: response.page ?? 1,
        folders,
      };
    },
    [currentViewMode, onAvailablePrinterModelsChange] // Include currentViewMode and callback in dependencies
  );

  const config: UseFileBrowserConfig<GcodeFile> = useMemo(
    () => {
      if (typeof window !== 'undefined' && (window as { PrintFarmerDebug?: { gcodeFileBrowser?: boolean } }).PrintFarmerDebug?.gcodeFileBrowser) {
        console.log('[GcodeFileBrowser] Config useMemo recalculating with currentViewMode:', currentViewMode);
      }
      return {
        fetcher,
        mapQueryParams: (query: FileQueryState) => {
          const params: Record<string, unknown> = {
            viewMode: currentViewMode,
            harvestId,
            printerId,
            sortBy: query.sortBy as 'name' | 'size' | 'date',
            sortOrder: query.sortOrder,
            search: query.search,
            page: query.page,
            pageSize: query.pageSize,
            tagIds: selectedTags.length > 0 ? selectedTags : undefined,
            printerModels: selectedPrinterModels.length > 0 ? selectedPrinterModels : undefined,
          };
          
          // Only include path for explorer mode (grid mode omits it for "all files")
          if (currentViewMode === 'explorer') {
            params.path = query.path;
          }
          
          if (typeof window !== 'undefined' && (window as { PrintFarmerDebug?: { gcodeFileBrowser?: boolean } }).PrintFarmerDebug?.gcodeFileBrowser) {
            console.log('[GcodeFileBrowser] mapQueryParams called, returning:', params);
          }
          return params;
        },
        mapDomainToFileItem: toGcodeFileItem,
        defaultSort: { sortBy: 'name', sortOrder: 'asc' },
        selectedIds: selection,
        onSelectionChange: handleSelectionChange,
        canCreateDirectory: hasPermission('gcode_harvest', 'create'),
        onCreateDirectory: async (path: string, folderName: string) => {
          try {
            const fullPath = path === '/' ? `/${folderName}` : `${path}/${folderName}`;
            const result = await apiClient.createGcodeDirectory(fullPath);
            if (!result.success) {
              throw new Error(result.message || 'Failed to create directory');
            }
            toast.success(`Created folder "${folderName}"`);
          } catch (error) {
            const msg = error instanceof Error ? error.message : 'Failed to create folder';
            toast.error(msg);
            throw error;
          }
        },
        onMoveFiles: async (fileIds: string[], targetPath: string) => {
          try {
            const response = await apiClient.moveGcodeFiles(fileIds, targetPath);
            if (response.success) {
              toast.success(`Moved ${fileIds.length} file${fileIds.length > 1 ? 's' : ''}`);
            } else {
              toast.error(response.message || 'Failed to move files');
            }
          } catch (error) {
            const msg = error instanceof Error ? error.message : 'Failed to move files';
            toast.error(msg);
            throw error;
          }
        },
        initialPath: '/',
        viewMode: currentViewMode,
        onViewModeChange: handleViewModeChange,
      };
    },
    [fetcher, harvestId, hasPermission, printerId, currentViewMode, handleViewModeChange, selectedTags, selectedPrinterModels, selection, handleSelectionChange]
  );

  const extraToolbarActions = (
    <>
      {hasPermission('gcode_harvest', 'create') && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          title="Upload G-code files"
          iconCenter={<UploadIcon className="h-4 w-4" />}
          onClick={() => setShowUploadModal(true)}
          data-tour="gcode-upload"
        />
      )}
      {onToggleTagFilterPanel && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          onClick={onToggleTagFilterPanel}
          title="Filter by tags"
          iconCenter={<FilterIcon className="h-4 w-4" />}
          data-tour="gcode-filters"
        />
      )}
      {selection.length > 0 && onShowTagModal && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          onClick={onShowTagModal}
          title={`Tag ${selection.length} selected file${selection.length > 1 ? 's' : ''}`}
          iconLeft={<TagIcon className="h-4 w-4" />}
        >
          Tag ({selection.length})
        </Button>
      )}
      {selection.length > 0 && onShowAddToProjectModal && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          onClick={onShowAddToProjectModal}
          title={`Add ${selection.length} selected file${selection.length > 1 ? 's' : ''} to a project`}
          iconLeft={<ClipboardListIcon className="h-4 w-4" />}
        >
          Add to Project ({selection.length})
        </Button>
      )}
      {selection.length > 0 && hasPermission('gcode_harvest', 'delete') && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          className="text-pf-error hover:text-pf-error hover:bg-pf-error/10"
          onClick={handleBulkDeleteClick}
          title={`Delete ${selection.length} selected file${selection.length > 1 ? 's' : ''}`}
          iconLeft={<DeleteIcon className="h-4 w-4" />}
        >
          Delete ({selection.length})
        </Button>
      )}
    </>
  );



  const handleUpload = useCallback(
    async (
      files: File[],
      onProgress?: (fileName: string, progress: number) => void,
      onItemComplete?: (fileName: string, status: 'done' | 'error', error?: string) => void
    ) => {
      try {
        // Establish SignalR connection BEFORE starting uploads to avoid race condition
        await signalRService.connect();
        
        // Set up listeners for upload progress
        const unsubscribeUploadProgress = signalRService.onSingleFileHarvestProgress((event) => {
          onProgress?.(event.fileName, event.percentComplete);
        });
        
        const unsubscribeUploadComplete = signalRService.onSingleFileHarvestComplete((event) => {
          onItemComplete?.(
            event.fileName,
            event.success ? 'done' : 'error',
            event.success ? undefined : event.message
          );
        });
        
        try {
          // NOW start uploading files sequentially (listeners are in place)
          let succeededCount = 0;
          let failedCount = 0;

          for (const file of files) {
            try {
              await apiClient.uploadGcodeLibraryFile(file, "/", onProgress);
              succeededCount++;
              // Mark as done after successful upload
              onItemComplete?.(file.name, 'done');
            } catch (error) {
              failedCount++;
              const errorMsg = error instanceof Error ? error.message : 'Unknown error';
              onItemComplete?.(file.name, 'error', errorMsg);
            }
          }
          
          if (succeededCount && failedCount) {
            // Mixed results
            toast.warning(`${succeededCount} uploaded, ${failedCount} failed`);
          } else if (succeededCount) {
            toast.success(`Uploaded ${succeededCount} file${succeededCount > 1 ? 's' : ''}`);
          } else if (failedCount) {
            toast.error(`Failed to upload ${failedCount} file${failedCount > 1 ? 's' : ''}`);
          }
          
          // Invalidate file browser cache to refresh the list
          if (succeededCount) {
            queryClient.invalidateQueries({ queryKey: ['file-browser'] });
          }
        } finally {
          // Clean up listeners
          unsubscribeUploadProgress();
          unsubscribeUploadComplete();
        }
      } catch (error) {
        toast.error(`Upload failed: ${error instanceof Error ? error.message : 'Unknown error'}`);
      } finally {
        setShowUploadModal(false);
      }
    },
    [queryClient]
  );

  return (
    <>
      <FileBrowser
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        config={config as any}
        sortOptions={sortOptions}
        columns={gcodeColumns}
        extraToolbarActions={extraToolbarActions}
        viewMode={viewMode}
        onViewModeChange={onViewModeChange}
        renderItemActions={renderActions}
        renderMetadata={renderMetadata}
        renderCard={renderGcodeCard}
      />
      <GcodeUploadModal
        isOpen={showUploadModal}
        onClose={() => setShowUploadModal(false)}
        onFilesSelected={handleUpload}
        harvestId={harvestId}
        printerId={printerId}
      />
      {queueFileToAdd && (
        <QueueGcodeModal
          file={queueFileToAdd}
          isOpen={showQueueModal}
          onClose={() => {
            setShowQueueModal(false);
            setQueueFileToAdd(null);
          }}
        />
      )}
      <ConfirmationModal
        isOpen={deleteConfirm.isOpen}
        title="Delete File"
        message={deleteConfirm.file ? `Are you sure you want to delete "${deleteConfirm.file.fileName}"?` : ''}
        confirmButtonText="Delete"
        cancelButtonText="Cancel"
        isDangerous
        onConfirm={handleDeleteConfirm}
        onCancel={handleDeleteCancel}
      />
      <ConfirmationModal
        isOpen={bulkDeleteConfirm}
        title="Delete Selected Files"
        message={`Are you sure you want to delete ${selection.length} selected file${selection.length > 1 ? 's' : ''}? This action cannot be undone.`}
        confirmButtonText={`Delete ${selection.length} File${selection.length > 1 ? 's' : ''}`}
        cancelButtonText="Cancel"
        isDangerous
        onConfirm={handleBulkDeleteConfirm}
        onCancel={handleBulkDeleteCancel}
      />
    </>
  );
};
