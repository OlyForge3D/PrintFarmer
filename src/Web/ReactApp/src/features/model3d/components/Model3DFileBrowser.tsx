import { useCallback, useMemo, useState, useOptimistic, useTransition } from 'react';
import { toast } from 'sonner';
import { FileBrowser } from '@/features/fileBrowser/components/FileBrowser';
import { type ColumnDef, type FileQueryState, type Model3DFileItem, type UseFileBrowserConfig } from '@/features/fileBrowser/types';
import { apiClient } from '@/services/api';
import { useAuth } from '@/features/auth/hooks/useAuth';
import type { Model3DFile, Model3DListResponse } from '@/types/api';

const toModel3DFileItem = (file: Model3DFile): Model3DFileItem => ({
  id: file.path, // Use path as ID for 3D model files
  path: file.path,
  fileName: file.name,
  isDirectory: file.isDirectory,
  fileSize: file.size,
  uploadedAt: file.modifiedAt?.toString(),
  thumbnailUrl: file.thumbnailUrl,
  meta: {
    model3d: { ...file } as Record<string, unknown>,
  },
});

const formatBytes = (bytes?: number) => {
  if (!bytes) return '—';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${(bytes / Math.pow(k, i)).toFixed(1)} ${sizes[i]}`;
};

const model3dColumns: ColumnDef[] = [
  { key: 'fileName', label: 'Name', sortable: true },
  {
    key: 'size',
    label: 'Size',
    sortable: true,
    align: 'right',
    render: (file) => formatBytes(file.fileSize),
  },
  {
    key: 'modifiedDate',
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
          className="h-12 w-12 rounded object-cover"
        />
      ) : (
        '—'
      ),
  },
];

interface Model3DFileBrowserProps {
  viewMode?: 'grid' | 'explorer';
  onViewModeChange?: (mode: 'grid' | 'explorer') => void;
}

export const Model3DFileBrowser = ({
  viewMode,
  onViewModeChange,
}: Model3DFileBrowserProps) => {
  const { hasPermission } = useAuth();
  const [currentViewMode, setCurrentViewMode] = useState<'grid' | 'explorer'>(viewMode ?? 'grid');
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const [isPending, startTransition] = useTransition();
  
  // For optimistic deletes, track deleted file IDs with useOptimistic
  // Reducer: removes the deleted IDs from the set
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const [optimisticDeletedIds, addOptimisticDelete] = useOptimistic(
    new Set<string>(),
    (state: Set<string>, deletedId: string) => {
      const newState = new Set(state);
      newState.add(deletedId);
      return newState;
    }
  );

  const sortOptions = useMemo(
    () => [
      { value: 'name', label: 'Name' },
      { value: 'size', label: 'Size' },
      { value: 'date', label: 'Date' },
    ],
    []
  );

  const fetcher = useCallback(
    async (params: ReturnType<NonNullable<UseFileBrowserConfig<Model3DFile>['mapQueryParams']>>) => {
      const params_typed = params as { path?: string; sortBy?: string; sortOrder?: string; search?: string; page?: number; pageSize?: number };
      const response: Model3DListResponse = await (
        apiClient as unknown as {
          listModelsHierarchical: (path: string, sortBy: string, sortOrder: string, search?: string, page?: number, pageSize?: number) => Promise<Model3DListResponse>;
        }
      ).listModelsHierarchical(
        params_typed.path || '/',
        params_typed.sortBy || 'name',
        params_typed.sortOrder || 'asc',
        params_typed.search,
        params_typed.page || 1,
        params_typed.pageSize || 50
      );
      
      // Build folder tree from separate folders endpoint in explorer mode
      let folders: Array<{ path: string; name: string; children?: Array<{ path: string; name: string }> }> = [];
      if (currentViewMode === 'explorer') {
        const folderEntries = await (
          apiClient as unknown as {
            listModelsFolders: () => Promise<Array<{ path: string; fileName: string; isDirectory: boolean }>>;
          }
        ).listModelsFolders();
        
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
        totalItems: response.totalFiles ?? 0,
        totalPages: response.totalPages ?? 1,
        totalSize: response.totalSize ?? 0,
        page: response.page ?? 1,
        folders,
      };
    },
    [currentViewMode]
  );

  const config: UseFileBrowserConfig<Model3DFile> = useMemo(
    () => ({
      fetcher,
      mapQueryParams: (query: FileQueryState) => ({
        path: currentViewMode === 'grid' ? null : query.path,
        sortBy: query.sortBy as 'name' | 'size' | 'date',
        sortOrder: query.sortOrder,
        search: query.search,
        page: query.page,
        pageSize: query.pageSize,
      }),
      mapDomainToFileItem: toModel3DFileItem,
      defaultSort: { sortBy: 'name', sortOrder: 'asc' },
      canDelete: hasPermission('model_3d', 'delete'),
      canDownload: true,
      onDelete: async (ids: string[]) => {
        // Handle delete with optimistic UI update
        startTransition(async () => {
          // Show optimistic delete immediately for each ID
          for (const id of ids) {
            addOptimisticDelete(id);
          }
          
          try {
            // Execute deletion in background
            await Promise.all(ids.map((id) => apiClient.deleteModel(id)));
            // On success, state remains optimistically deleted
          } catch (error) {
            // On error, state rolls back automatically via useOptimistic
            const message = error instanceof Error ? error.message : 'Failed to delete model file';
            console.error('Delete model error:', message);
          }
        });
      },
      onDownload: async (id: string) => {
        // TODO: Implement download for 3D models when API method is available
        if (window.PrintFarmerDebug?.enabled) {
          console.log('Download 3D model:', id);
        }
      },
      onMoveFiles: async (fileIds: string[], targetPath: string) => {
        try {
          const response = await apiClient.moveModel3dFiles(fileIds, targetPath);
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
      onViewModeChange: (mode) => {
        setCurrentViewMode(mode);
        onViewModeChange?.(mode);
      },
    }),
    [fetcher, hasPermission, currentViewMode, onViewModeChange, addOptimisticDelete]
  );

  return (
    <FileBrowser
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      config={config as any}
      sortOptions={sortOptions}
      columns={model3dColumns}
      viewMode={viewMode}
      onViewModeChange={onViewModeChange}
    />
  );
};
