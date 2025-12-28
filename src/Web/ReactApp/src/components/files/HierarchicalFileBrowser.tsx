import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { ChevronRightIcon, FolderIcon, DocumentIcon, FolderPlusIcon, TrashIcon } from '@heroicons/react/24/outline';
import { Button, Checkbox, Input, Select } from '@/components/ui';
import { toast } from 'sonner';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import { TreeView, type TreeNode } from './TreeView';

export interface FileEntry {
  path: string;
  name: string;
  size: number;
  modifiedAt: string;
  isDirectory: boolean;
  thumbnailUrl?: string;
  harvestOperationId?: string;
  thumbnailPath?: string;
}

export interface HierarchicalBrowserResponse {
  files: FileEntry[];
  totalFiles: number;
  totalSize: number;
  page?: number;
  pageSize?: number;
  totalPages?: number;
  totalItems?: number;
}

export interface HierarchicalFileBrowserProps {
  /** API endpoint path (e.g., '/3d-models' or '/gcode-files') */
  endpoint: 'models' | 'gcode';
  /** Called when user downloads/selects a file */
  onFileSelect?: (file: FileEntry) => void;
  /** Called when user deletes files */
  onFileDelete?: (paths: string[]) => void;
  /** Initial path to start browsing from */
  initialPath?: string;
  /** Show thumbnails in list */
  showThumbnails?: boolean;
}

const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

const getApiPath = (endpoint: 'models' | 'gcode'): string => {
  return endpoint === 'models' ? '/3d-models' : '/gcode-files';
};

export const HierarchicalFileBrowser: React.FC<HierarchicalFileBrowserProps> = ({
  endpoint,
  onFileSelect,
  onFileDelete,
  initialPath = '/',
  showThumbnails = false,
}) => {
  const [currentPath, setCurrentPath] = useState(initialPath);
  const [selectedFiles, setSelectedFiles] = useState<string[]>([]);
  const [sortBy, setSortBy] = useState<'name' | 'size' | 'date'>('name');
  const [sortOrder, setSortOrder] = useState<'asc' | 'desc'>('asc');
  const [searchTerm, setSearchTerm] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize] = useState(50);
  const [showNewFolderDialog, setShowNewFolderDialog] = useState(false);
  const [newFolderName, setNewFolderName] = useState('');
  const [dragOverPath, setDragOverPath] = useState<string | null>(null);
  const [isDragging, setIsDragging] = useState(false);
  const [viewMode, setViewMode] = useState<'table' | 'tree'>('tree'); // Default to tree view
  const queryClient = useQueryClient();

  const apiPath = getApiPath(endpoint);
  const baseUrl = getApiBaseUrl();

  // Fetch files for current path
  const { data: fileData, isLoading } = useQuery<HierarchicalBrowserResponse>({
    queryKey: [`${endpoint}-files`, currentPath, sortBy, sortOrder, searchTerm, page, pageSize],
    queryFn: async () => {
      const listEndpoint = endpoint === 'models'
        ? `${baseUrl}${apiPath}/hierarchy`
        : `${baseUrl}${apiPath}/list`;
      
      const params = new URLSearchParams({
        path: currentPath,
        sortBy,
        sortOrder,
        page: String(page),
        pageSize: String(pageSize),
      });
      if (searchTerm) params.append('search', searchTerm);

      const response = await fetch(`${listEndpoint}?${params}`, {
        headers: getAuthHeaders(),
      });
      if (!response.ok) throw new Error(`Failed to fetch ${endpoint} files`);
      return response.json();
    },
    staleTime: 30000,
  });

  // Delete mutation
  const deleteMutation = useMutation({
    mutationFn: async (paths: string[]) => {
      const response = await fetch(`${baseUrl}${apiPath}`, {
        method: 'DELETE',
        headers: {
          'Content-Type': 'application/json',
          ...getAuthHeaders(),
        },
        body: JSON.stringify({ filePaths: paths }),
      });
      if (!response.ok) throw new Error(`Failed to delete files`);
      return response.json();
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: [`${endpoint}-files`] });
      setSelectedFiles([]);
      toast.success('Files deleted successfully');
      onFileDelete?.(selectedFiles);
    },
    onError: () => {
      toast.error('Failed to delete files');
    },
  });

  // Create folder mutation
  const createFolderMutation = useMutation({
    mutationFn: async (folderName: string) => {
      // Send relative path without leading slash - backend will resolve it properly
      const newFolderPath = currentPath === '/' ? folderName : `${currentPath}/${folderName}`;
      console.log('[createFolderMutation] Creating folder at path:', newFolderPath);
      const response = await fetch(`${baseUrl}${apiPath}/folder`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...getAuthHeaders(),
        },
        body: JSON.stringify({ path: newFolderPath }),
      });
      console.log('[createFolderMutation] Response status:', response.status);
      if (!response.ok) throw new Error(`Failed to create folder`);
      return response.json();
    },
    onSuccess: () => {
      // Invalidate current path specifically to refresh the listing
      console.log('[createFolderMutation] Invalidating query key:', [`${endpoint}-files`, currentPath]);
      queryClient.invalidateQueries({ queryKey: [`${endpoint}-files`, currentPath] });
      toast.success('Folder created successfully');
      setShowNewFolderDialog(false);
      setNewFolderName('');
    },
    onError: () => {
      toast.error('Failed to create folder');
    },
  });

  // Move files mutation
  const moveFilesMutation = useMutation({
    mutationFn: async ({ files, targetPath }: { files: string[]; targetPath: string }) => {
      // Send relative path without leading slash - backend will resolve it properly
      const normalizedTargetPath = targetPath.startsWith('/') ? targetPath.slice(1) : targetPath;
      const response = await fetch(`${baseUrl}${apiPath}/move`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...getAuthHeaders(),
        },
        body: JSON.stringify({ filePaths: files, targetPath: normalizedTargetPath }),
      });
      if (!response.ok) throw new Error(`Failed to move files`);
      return response.json();
    },
    onSuccess: () => {
      // Invalidate current path to refresh the listing
      queryClient.invalidateQueries({ queryKey: [`${endpoint}-files`, currentPath] });
      setSelectedFiles([]);
      toast.success('Files moved successfully');
      setDragOverPath(null);
    },
    onError: () => {
      toast.error('Failed to move files');
      setDragOverPath(null);
    },
  });

  const files = fileData?.files || [];
  const directories = files.filter(f => f.isDirectory);
  const filesList = files.filter(f => !f.isDirectory);

  // Generate breadcrumbs - show actual folder structure
  const breadcrumbs = currentPath === '/' || currentPath === ''
    ? [{ path: '/', name: 'Models' }]
    : [{ path: '/', name: 'Models' }, ...currentPath.split('/').filter(Boolean).map((segment, idx, arr) => ({
        path: '/' + arr.slice(0, idx + 1).join('/'),
        name: segment,
      }))];

  const handleSelectAll = (checked: boolean) => {
    if (checked) {
      setSelectedFiles(filesList.map(f => f.path));
    } else {
      setSelectedFiles([]);
    }
  };

  const handleSelectFile = (path: string, checked: boolean) => {
    if (checked) {
      setSelectedFiles([...selectedFiles, path]);
    } else {
      setSelectedFiles(selectedFiles.filter(p => p !== path));
    }
  };

  const handleNavigate = (path: string) => {
    setCurrentPath(path);
    setSelectedFiles([]);
    setPage(1);
  };

  const handleDelete = () => {
    if (selectedFiles.length === 0) {
      toast.error('Please select files to delete');
      return;
    }
    if (confirm(`Delete ${selectedFiles.length} file(s)?`)) {
      deleteMutation.mutate(selectedFiles);
    }
  };

  const handleCreateFolder = () => {
    if (!newFolderName.trim()) {
      toast.error('Folder name cannot be empty');
      return;
    }
    createFolderMutation.mutate(newFolderName);
  };

  const handleDragOver = (e: React.DragEvent, dirPath: string) => {
    e.preventDefault();
    e.stopPropagation();
    setDragOverPath(dirPath);
  };

  const handleDragLeave = () => {
    setDragOverPath(null);
  };

  const handleDropOnFolder = (e: React.DragEvent, dirPath: string) => {
    e.preventDefault();
    e.stopPropagation();
    setDragOverPath(null);

    if (selectedFiles.length === 0) {
      toast.error('Please select files to move');
      return;
    }

    moveFilesMutation.mutate({ files: selectedFiles, targetPath: dirPath });
  };

  return (
    <div className="space-y-4">
      {/* Breadcrumb Navigation */}
      <div className="flex items-center gap-2 text-sm text-pf-text-secondary overflow-x-auto pb-2">
        {breadcrumbs.map((crumb, idx) => (
          <React.Fragment key={crumb.path}>
            {idx > 0 && <ChevronRightIcon className="w-4 h-4 flex-shrink-0" />}
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={() => handleNavigate(crumb.path)}
              className="text-pf-link hover:text-pf-link-hover font-medium whitespace-nowrap !p-0"
            >
              {crumb.name}
            </Button>
          </React.Fragment>
        ))}
      </div>

      {/* Search and Controls */}
      <div className="flex gap-2 flex-wrap items-center">
        <Input
          type="text"
          placeholder="Search files..."
          value={searchTerm}
          onChange={(e) => {
            setSearchTerm(e.target.value);
            setPage(1);
          }}
          className="flex-1 min-w-48"
        />
        
        {/* View Mode Toggle */}
        <div className="flex gap-1 bg-pf-bg-1 border border-pf-border rounded p-1">
          <Button
            variant={viewMode === 'tree' ? 'primary' : 'secondary'}
            size="sm"
            onClick={() => setViewMode('tree')}
            title="Tree view"
            className="px-2"
          >
            <FolderIcon className="w-4 h-4" />
          </Button>
          <Button
            variant={viewMode === 'table' ? 'primary' : 'secondary'}
            size="sm"
            onClick={() => setViewMode('table')}
            title="Table view"
            className="px-2"
          >
            <DocumentIcon className="w-4 h-4" />
          </Button>
        </div>

        <Select value={sortBy} onChange={(e) => setSortBy(e.target.value as 'name' | 'size' | 'date')} className="w-32">
          <option value="name">Name</option>
          <option value="size">Size</option>
          <option value="date">Date</option>
        </Select>
        <Select value={sortOrder} onChange={(e) => setSortOrder(e.target.value as 'asc' | 'desc')} className="w-32">
          <option value="asc">Ascending</option>
          <option value="desc">Descending</option>
        </Select>
        <Button
          variant="secondary"
          size="sm"
          onClick={() => setShowNewFolderDialog(true)}
          title="Create new folder"
        >
          <FolderPlusIcon className="w-4 h-4 mr-1" />
          New Folder
        </Button>
        {selectedFiles.length > 0 && (
          <Button variant="danger" size="sm" onClick={handleDelete} disabled={deleteMutation.isPending}>
            {deleteMutation.isPending ? 'Deleting...' : `Delete (${selectedFiles.length})`}
          </Button>
        )}
      </div>

      {/* Drag and Drop Hint */}
      {selectedFiles.length > 0 && (
        <div className="flex items-center gap-2 px-4 py-3 bg-pf-info bg-opacity-10 border border-pf-info rounded-lg text-sm text-pf-info">
          <svg className="w-4 h-4 flex-shrink-0" fill="currentColor" viewBox="0 0 20 20">
            <path fillRule="evenodd" d="M18 5v8a2 2 0 01-2 2h-5l-5 4v-4H4a2 2 0 01-2-2V5a2 2 0 012-2h12a2 2 0 012 2zm-11-1a1 1 0 11-2 0 1 1 0 012 0z" clipRule="evenodd" />
          </svg>
          <span><strong>Tip:</strong> Drag selected files onto a folder to move them</span>
        </div>
      )}

      {/* New Folder Dialog */}
      {showNewFolderDialog && (
        <div className="bg-pf-bg-2 border border-pf-border rounded-lg p-4 space-y-3">
          <div className="flex items-center gap-2">
            <input
              type="text"
              placeholder="Folder name..."
              value={newFolderName}
              onChange={(e) => setNewFolderName(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') handleCreateFolder();
                if (e.key === 'Escape') setShowNewFolderDialog(false);
              }}
              className="flex-1 px-3 py-2 border border-pf-border rounded-md text-sm bg-pf-bg-1 text-pf-text focus:outline-none focus:ring-1 focus:ring-pf-primary"
              autoFocus
            />
            <Button
              size="sm"
              onClick={handleCreateFolder}
              disabled={createFolderMutation.isPending || !newFolderName.trim()}
            >
              {createFolderMutation.isPending ? 'Creating...' : 'Create'}
            </Button>
            <Button
              variant="secondary"
              size="sm"
              onClick={() => setShowNewFolderDialog(false)}
            >
              Cancel
            </Button>
          </div>
        </div>
      )}

      {/* File List */}
      <div className="border border-pf-border rounded-lg overflow-hidden">
        {isLoading ? (
          <div className="p-8 text-center text-pf-text-secondary">Loading files...</div>
        ) : files.length === 0 ? (
          <div className="p-8 text-center text-pf-text-secondary">
            {searchTerm ? 'No files match your search' : 'This folder is empty'}
          </div>
        ) : viewMode === 'tree' ? (
          // Tree View
          <div className="p-4">
            <TreeView
              nodes={files.map(f => ({
                path: f.path,
                name: f.name,
                isDirectory: f.isDirectory,
                size: f.size,
                modifiedAt: f.modifiedAt,
                thumbnailUrl: f.thumbnailUrl,
              }))}
              onSelect={(node) => {
                if (!node.isDirectory) {
                  onFileSelect?.(files.find(f => f.path === node.path) || (node as FileEntry));
                }
              }}
              onNavigate={handleNavigate}
              currentPath={currentPath}
              selectedFiles={selectedFiles}
              onSelectFile={handleSelectFile}
              isLoading={isLoading}
            />
          </div>
        ) : (
          // Table View
          <table className="w-full">
            <thead className="bg-pf-bg-2 border-b border-pf-border sticky top-0">
              <tr>
                <th className="px-4 py-3 text-left w-8">
                  {filesList.length > 0 && (
                    <Checkbox
                      checked={selectedFiles.length === filesList.length && filesList.length > 0}
                      onChange={(e) => handleSelectAll(e.currentTarget.checked)}
                    />
                  )}
                </th>
                <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Name</th>
                <th className="px-4 py-3 text-right font-semibold text-pf-text-primary">Size</th>
                <th className="px-4 py-3 text-center font-semibold text-pf-text-primary">Modified</th>
                <th className="px-4 py-3 text-right font-semibold text-pf-text-primary">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-pf-border">
              {/* Directories First */}
              {directories.map((dir) => (
                <tr
                  key={dir.path}
                  className={`hover:bg-pf-bg-2 transition-colors ${
                    selectedFiles.length > 0 ? 'cursor-move' : 'cursor-pointer'
                  } ${
                    dragOverPath === dir.path 
                      ? 'bg-pf-primary bg-opacity-15 border-l-4 border-pf-primary' 
                      : ''
                  }`}
                  draggable={false}
                  onDragOver={(e) => handleDragOver(e, dir.path)}
                  onDragLeave={handleDragLeave}
                  onDrop={(e) => handleDropOnFolder(e, dir.path)}
                >
                  <td className="px-4 py-3" />
                  <td
                    className="px-4 py-3 font-medium text-pf-link hover:text-pf-link-hover flex items-center gap-2"
                    onClick={() => handleNavigate(dir.path)}
                  >
                    <FolderIcon className={`w-5 h-5 flex-shrink-0 ${
                      dragOverPath === dir.path ? 'text-pf-primary' : ''
                    }`} />
                    {dir.name}
                  </td>
                  <td className="px-4 py-3 text-right text-pf-text-secondary">—</td>
                  <td className="px-4 py-3 text-center text-pf-text-secondary text-sm">
                    {new Date(dir.modifiedAt).toLocaleDateString()}
                  </td>
                  <td className="px-4 py-3 text-right" />
                </tr>
              ))}

              {/* Files */}
              {filesList.map((file) => (
                <tr 
                  key={file.path} 
                  className={`hover:bg-pf-bg-2 transition-colors ${
                    selectedFiles.includes(file.path) ? 'bg-pf-primary bg-opacity-10' : ''
                  }`}
                  draggable={selectedFiles.includes(file.path) && selectedFiles.length > 0}
                  onDragStart={() => setIsDragging(true)}
                  onDragEnd={() => setIsDragging(false)}
                >
                  <td className="px-4 py-3">
                    <Checkbox
                      checked={selectedFiles.includes(file.path)}
                      onChange={(e) => handleSelectFile(file.path, e.currentTarget.checked)}
                    />
                  </td>
                  <td className="px-4 py-3 flex items-center gap-3">
                    {showThumbnails && file.thumbnailUrl && (
                      <img
                        src={file.thumbnailUrl}
                        alt={file.name}
                        className="w-10 h-10 rounded object-cover flex-shrink-0"
                      />
                    )}
                    <div>
                      <div className="font-medium text-pf-text-primary">{file.name}</div>
                      <div className="text-xs text-pf-text-tertiary">{formatBytes(file.size)}</div>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-right text-pf-text-secondary">{formatBytes(file.size)}</td>
                  <td className="px-4 py-3 text-center text-pf-text-secondary text-sm">
                    {new Date(file.modifiedAt).toLocaleDateString()}
                  </td>
                  <td className="px-4 py-3 text-right flex justify-end gap-2">
                    {onFileSelect && (
                      <Button
                        type="button"
                        variant="subtle"
                        size="sm"
                        onClick={() => onFileSelect(file)}
                        className="text-pf-link hover:text-pf-link-hover !p-0"
                        title="Select"
                        aria-label={`Select ${file.name}`}
                      >
                        <DocumentIcon className="w-5 h-5" />
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {/* Pagination */}
      {fileData && fileData.totalPages && fileData.totalPages > 1 && (
        <div className="flex items-center justify-between text-sm">
          <div className="text-pf-text-secondary">
            Showing {filesList.length} files ({formatBytes(fileData.totalSize)} total)
          </div>
          <div className="flex gap-2">
            <Button
              size="sm"
              disabled={page === 1}
              onClick={() => setPage(p => p - 1)}
            >
              Previous
            </Button>
            <div className="flex items-center gap-2 px-2 text-pf-text-secondary">
              Page {page} of {fileData.totalPages}
            </div>
            <Button
              size="sm"
              disabled={page === fileData.totalPages}
              onClick={() => setPage(p => p + 1)}
            >
              Next
            </Button>
          </div>
        </div>
      )}
    </div>
  );
};
