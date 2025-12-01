import React, { useState, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { ChevronRightIcon, FolderIcon, DocumentIcon, ArrowDownTrayIcon, TrashIcon } from '@heroicons/react/24/outline';
import { Button, Checkbox, Input, Select } from '@/components/ui';
import { toast } from 'sonner';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import styles from './FileBrowser.module.css';

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
  const [pageSize, setPageSize] = useState(50);
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

  const files = fileData?.files || [];
  const directories = files.filter(f => f.isDirectory);
  const filesList = files.filter(f => !f.isDirectory);

  const breadcrumbs = currentPath === '/' 
    ? [{ path: '/', name: 'Root' }]
    : [{ path: '/', name: 'Root' }, ...currentPath.split('/').filter(Boolean).map((segment, idx, arr) => ({
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

  return (
    <div className="space-y-4">
      {/* Breadcrumb Navigation */}
      <div className="flex items-center gap-2 text-sm text-pf-text-secondary overflow-x-auto pb-2">
        {breadcrumbs.map((crumb, idx) => (
          <React.Fragment key={crumb.path}>
            {idx > 0 && <ChevronRightIcon className="w-4 h-4 flex-shrink-0" />}
            <button
              onClick={() => handleNavigate(crumb.path)}
              className="text-pf-link hover:text-pf-link-hover font-medium whitespace-nowrap"
            >
              {crumb.name}
            </button>
          </React.Fragment>
        ))}
      </div>

      {/* Search and Controls */}
      <div className="flex gap-2 flex-wrap">
        <Input
          type="text"
          placeholder="Search files..."
          value={searchTerm}
          onChange={(e) => {
            setSearchTerm(e.target.value);
            setPage(1);
          }}
          className="flex-1 min-w-64"
        />
        <Select value={sortBy} onChange={(e) => setSortBy(e.target.value as any)}>
          <option value="name">Name</option>
          <option value="size">Size</option>
          <option value="date">Date</option>
        </Select>
        <Select value={sortOrder} onChange={(e) => setSortOrder(e.target.value as any)}>
          <option value="asc">Ascending</option>
          <option value="desc">Descending</option>
        </Select>
        {selectedFiles.length > 0 && (
          <Button variant="danger" size="sm" onClick={handleDelete} disabled={deleteMutation.isPending}>
            {deleteMutation.isPending ? 'Deleting...' : `Delete (${selectedFiles.length})`}
          </Button>
        )}
      </div>

      {/* File List */}
      <div className="border border-pf-border rounded-lg overflow-hidden">
        {isLoading ? (
          <div className="p-8 text-center text-pf-text-secondary">Loading files...</div>
        ) : files.length === 0 ? (
          <div className="p-8 text-center text-pf-text-secondary">
            {searchTerm ? 'No files match your search' : 'This folder is empty'}
          </div>
        ) : (
          <table className="w-full">
            <thead className="bg-pf-bg-2 border-b border-pf-border">
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
                <tr key={dir.path} className="hover:bg-pf-bg-2 transition-colors">
                  <td className="px-4 py-3" />
                  <td
                    className="px-4 py-3 cursor-pointer font-medium text-pf-link hover:text-pf-link-hover flex items-center gap-2"
                    onClick={() => handleNavigate(dir.path)}
                  >
                    <FolderIcon className="w-5 h-5 flex-shrink-0" />
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
                <tr key={file.path} className="hover:bg-pf-bg-2 transition-colors">
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
                      <div className="text-xs text-pf-text-tertiary">{file.path}</div>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-right text-pf-text-secondary">{formatBytes(file.size)}</td>
                  <td className="px-4 py-3 text-center text-pf-text-secondary text-sm">
                    {new Date(file.modifiedAt).toLocaleDateString()}
                  </td>
                  <td className="px-4 py-3 text-right flex justify-end gap-2">
                    {onFileSelect && (
                      <button
                        onClick={() => onFileSelect(file)}
                        className="text-pf-link hover:text-pf-link-hover"
                        title="Select"
                      >
                        <DocumentIcon className="w-5 h-5" />
                      </button>
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
