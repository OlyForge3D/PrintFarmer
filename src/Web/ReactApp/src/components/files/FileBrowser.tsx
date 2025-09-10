import React, { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { ChevronRightIcon } from '@heroicons/react/24/outline';

import { GcodeFile, GetGcodeFilesResponse } from '@/types/api';
import { useAuth } from '@/contexts/AuthHooks';
import { apiClient } from '@/services/api';
import { FileRow } from './FileRow';

interface FileBrowserProps {
  harvestId?: string;
  printerId?: string;
  initialPath?: string;
}

// Utility function to format bytes
const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 Bytes';
  const k = 1024;
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

export const FileBrowser: React.FC<FileBrowserProps> = ({
  harvestId,
  printerId,
  initialPath = '/'
}) => {
  const { hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const [currentPath, setCurrentPath] = useState(initialPath);
  const [selectedFiles, setSelectedFiles] = useState<string[]>([]);
  const [viewMode, setViewMode] = useState<'grid' | 'list'>('list');
  const [sortBy, setSortBy] = useState<'name' | 'size' | 'date'>('name');
  const [sortOrder, setSortOrder] = useState<'asc' | 'desc'>('asc');
  const [searchTerm, setSearchTerm] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);

  const { data: files, isLoading } = useQuery<GetGcodeFilesResponse>({
    queryKey: ['gcode-files', currentPath, harvestId, printerId, sortBy, sortOrder, searchTerm, page, pageSize],
    queryFn: () => apiClient.getGcodeFilesWithFilter({
      path: currentPath,
      harvestId,
      printerId,
      sortBy,
      sortOrder,
      search: searchTerm,
      page,
      pageSize
    }),
  });

  const deleteMutation = useMutation({
    mutationFn: (filePaths: string[]) => apiClient.deleteGcodeFiles(filePaths),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['gcode-files'] });
      setSelectedFiles([]);
      toast.success('Files deleted successfully');
    },
    onError: () => {
      toast.error('Failed to delete files');
    }
  });

  const downloadMutation = useMutation({
    mutationFn: (filePath: string) => apiClient.downloadGcodeFile(filePath),
    onSuccess: () => {
      toast.success('Download started');
    },
    onError: () => {
      toast.error('Failed to download file');
    }
  });

  const handleSelectAll = () => {
    if (selectedFiles.length === (files?.files?.length ?? 0)) {
      setSelectedFiles([]);
    } else {
      setSelectedFiles(files?.files?.map((f: GcodeFile) => f.path) || []);
    }
  };

  const handleDeleteSelected = async () => {
    if (selectedFiles.length === 0) return;
    
    if (confirm(`Delete ${selectedFiles.length} selected files?`)) {
      deleteMutation.mutate(selectedFiles);
    }
  };

  const breadcrumbs = currentPath.split('/').filter(Boolean);

  return (
    <div className="space-y-4">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center space-x-4">
          {/* Breadcrumbs */}
          <nav className="flex items-center space-x-2 text-sm">
            <button
              onClick={() => setCurrentPath('/')}
              className="text-blue-600 hover:text-blue-800"
            >
              Root
            </button>
            
            {breadcrumbs.map((segment, index) => (
              <React.Fragment key={index}>
                <ChevronRightIcon className="w-4 h-4 text-gray-400" />
                <button
                  onClick={() => { setCurrentPath('/' + breadcrumbs.slice(0, index + 1).join('/')); setPage(1);} }
                  className="text-blue-600 hover:text-blue-800"
                >
                  {segment}
                </button>
              </React.Fragment>
            ))}
          </nav>
        </div>

        {/* Actions */}
        <div className="flex items-center space-x-2">
          {selectedFiles.length > 0 && hasPermission('gcode_harvest', 'delete') && (
            <button
              onClick={handleDeleteSelected}
              disabled={deleteMutation.isPending}
              className="px-3 py-1 bg-red-600 text-white text-sm rounded hover:bg-red-700 disabled:opacity-50"
            >
              Delete Selected ({selectedFiles.length})
            </button>
          )}
          
          <div className="flex border border-gray-300 rounded">
            <button
              onClick={() => setViewMode('list')}
              className={`px-3 py-1 text-sm ${viewMode === 'list' ? 'bg-blue-500 text-white' : 'text-gray-700'}`}
            >
              List
            </button>
            <button
              onClick={() => setViewMode('grid')}
              className={`px-3 py-1 text-sm ${viewMode === 'grid' ? 'bg-blue-500 text-white' : 'text-gray-700'}`}
            >
              Grid
            </button>
          </div>
        </div>
      </div>

      {/* Search and filters */}
      <div className="flex items-center space-x-4">
        <div className="flex-1 max-w-md">
          <label htmlFor="file-search" className="sr-only">Search files</label>
          <input
            id="file-search"
            type="text"
            placeholder="Search files..."
            aria-label="Search files"
            value={searchTerm}
            onChange={(e) => { setSearchTerm(e.target.value); setPage(1);} }
            className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
        </div>
        
        <label htmlFor="sort-by" className="sr-only">Sort by</label>
        <select
          id="sort-by"
          aria-label="Sort files by"
          value={sortBy}
          onChange={(e) => { setSortBy(e.target.value as 'name' | 'size' | 'date'); setPage(1);} }
          className="px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
        >
          <option value="name">Sort by Name</option>
          <option value="size">Sort by Size</option>
          <option value="date">Sort by Date</option>
        </select>
        
        <button
          onClick={() => { setSortOrder(sortOrder === 'asc' ? 'desc' : 'asc'); setPage(1);} }
          className="px-3 py-2 border border-gray-300 rounded-md hover:bg-gray-50"
        >
          {sortOrder === 'asc' ? '↑' : '↓'}
        </button>
      </div>

      {/* File listing */}
      {isLoading ? (
        <div className="space-y-2">
          {[...Array(5)].map((_, i) => (
            <div key={i} className="h-16 bg-gray-200 rounded animate-pulse" />
          ))}
        </div>
  ) : files && files.files && files.files.length > 0 ? (
        <div className="bg-white rounded-lg shadow">
          {/* Table header */}
          <div className="px-4 py-3 border-b border-gray-200 flex items-center">
            <input
              type="checkbox"
              title="Select all files"
              aria-label="Select all files"
              checked={selectedFiles.length === (files?.files?.length ?? 0) && (files?.files?.length ?? 0) > 0}
              onChange={handleSelectAll}
              className="mr-4"
            />
            
            <div className="flex-1 grid grid-cols-12 gap-4 text-sm font-medium text-gray-500">
              <div className="col-span-5">Name</div>
              <div className="col-span-2">Size</div>
              <div className="col-span-3">Modified</div>
              <div className="col-span-2">Actions</div>
            </div>
          </div>

          {/* File rows */}
          <div className="divide-y divide-gray-200">
            {files.files?.map((file: GcodeFile) => (
              <FileRow
                key={file.path}
                file={file}
                selected={selectedFiles.includes(file.path)}
                onSelect={(selected) => {
                  if (selected) {
                    setSelectedFiles(prev => [...prev, file.path]);
                  } else {
                    setSelectedFiles(prev => prev.filter(p => p !== file.path));
                  }
                }}
                onDownload={() => downloadMutation.mutate(file.path)}
                onDelete={() => {
                  if (confirm(`Delete ${file.name}?`)) {
                    deleteMutation.mutate([file.path]);
                  }
                }}
                onNavigate={file.isDirectory ? () => { setCurrentPath(file.path); setPage(1);} : undefined}
              />
            ))}
          </div>
        </div>
      ) : (
        <div className="bg-white rounded-lg shadow p-8 text-center text-gray-500">
          {searchTerm ? 'No files match your search' : 'No files found'}
        </div>
      )}

      {/* File count and size */}
      {files && (
        <div className="flex flex-col gap-2 text-sm text-gray-500">
          <div>
            {files.totalFiles} files • {formatBytes(files.totalSize)}
          </div>
          {/* Pagination controls */}
          <div className="flex items-center gap-3">
            <button
              disabled={page === 1}
              onClick={() => setPage(p => Math.max(1, p - 1))}
              className="px-2 py-1 border border-gray-300 rounded disabled:opacity-40"
            >Prev</button>
            <span>Page {(files.page ?? page)} of {(files.totalPages ?? '?')}</span>
            <button
              disabled={files.totalPages ? page >= (files.totalPages ?? 1) : ((files.files?.length ?? 0) < pageSize)}
              onClick={() => setPage(p => p + 1)}
              className="px-2 py-1 border border-gray-300 rounded disabled:opacity-40"
            >Next</button>
            <select
              aria-label="Select page size"
              value={pageSize}
              onChange={e => { setPageSize(Number(e.target.value)); setPage(1);} }
              className="px-2 py-1 border border-gray-300 rounded"
            >
              {[25,50,100,200,500].map(size => <option key={size} value={size}>{size}/page</option>)}
            </select>
          </div>
        </div>
      )}
    </div>
  );
};