/* eslint-disable @typescript-eslint/no-unused-vars */
import React, { useEffect, useState, ReactNode } from 'react';
import { useMutation, useQuery, useQueryClient, UseQueryResult } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Button, Input, Select } from '@/common/components/ui';
import { ConfirmationModal } from '@/common/components/modals';
import { FileBrowserViewModeToggle } from '@/common/components/FileBrowserViewModeToggle';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { useAuth } from '@/features/auth/hooks/useAuth';

// Generic file item interface
export interface FileItem {
  path: string;
  fileName: string;
  isDirectory: boolean;
  size: number;
  modifiedDate: string;
  [key: string]: unknown;
}

// Generic fetch response
export interface FetchFilesResponse<T extends FileItem = FileItem> {
  files: T[];
  totalFiles: number;
  totalSize: number;
  page: number;
  totalPages: number;
}

// Configuration for the browser
export interface GenericFileBrowserConfig<T extends FileItem = FileItem> {
  // Data fetching
  fetchFiles: (params: {
    path: string;
    search: string;
    sortBy: string;
    sortOrder: 'asc' | 'desc';
    page: number;
    pageSize: number;
  }) => Promise<FetchFilesResponse<T>>;

  // UI Components
  gridViewComponent: React.ComponentType<{
    files: T[];
    onNavigate: (path: string) => void;
    onDelete: (file: T) => void;
    onDownload?: (path: string) => void;
    isDeleting: boolean;
  }>;

  explorerViewComponent: React.ComponentType<{
    files: T[];
    isLoading: boolean;
    selectedFiles: string[];
    onSelectFile: (path: string) => void;
    onSelectAll: (files: T[]) => void;
    currentPath: string;
    onNavigate: (path: string) => void;
    sortBy?: string;
    sortOrder?: 'asc' | 'desc';
    onSort?: (sortBy: string) => void;
  }>;

  // Callbacks
  onDelete?: (paths: string[]) => Promise<void>;
  onDownload?: (path: string) => Promise<void>;
  canDelete?: boolean;
  canDownload?: boolean;

  // View preferences key
  viewModePreferenceKey?: string;
  
  // Sorting options
  sortOptions?: Array<{ value: string; label: string }>;
  defaultSort?: string;

  // Formatting
  formatBytes: (bytes: number) => string;
  formatDate: (date: string) => string;

  // Extra toolbar buttons (for page-specific buttons like Upload, Tag, Filter)
  extraToolbarButtons?: React.ReactNode;
}

export interface GenericFileBrowserProps<T extends FileItem = FileItem> {
  config: GenericFileBrowserConfig<T>;
  isModal?: boolean;
  viewMode?: 'grid' | 'explorer';
  onViewModeChange?: (mode: 'grid' | 'explorer') => void;
  initialPath?: string;
}

export const GenericFileBrowser = React.forwardRef<
  HTMLDivElement,
  GenericFileBrowserProps
>(
  (
    {
      config,
      isModal = false,
      viewMode: initialViewMode,
      onViewModeChange,
      initialPath = '/',
    },
    ref
  ) => {
    const { hasPermission } = useAuth();
    const queryClient = useQueryClient();
    const [currentPath, setCurrentPath] = useState(initialPath);
    const [selectedFiles, setSelectedFiles] = useState<string[]>([]);
    const { 
      viewMode: savedViewMode, 
      setViewMode: setSavedViewMode 
    } = useViewModePreference(config.viewModePreferenceKey || 'printfarmer-viewmode');

    const viewMode = (initialViewMode ?? (savedViewMode as 'grid' | 'explorer')) as 'grid' | 'explorer';
    const setViewMode = (mode: 'grid' | 'explorer') => {
      if (onViewModeChange) {
        onViewModeChange(mode);
      } else {
        setSavedViewMode(mode);
      }
    };

    const [sortBy, setSortBy] = useState(config.defaultSort || 'name');
    const [sortOrder, setSortOrder] = useState<'asc' | 'desc'>('asc');
    const [searchTerm, setSearchTerm] = useState('');
    const [page, setPage] = useState(1);
    const [pageSize, setPageSize] = useState(50);
    const [deleteConfirmDialog, setDeleteConfirmDialog] = useState<{
      isOpen: boolean;
      filesToDelete: FileItem[];
      fileName?: string;
    }>({ isOpen: false, filesToDelete: [] });

    // Fetch files
    const { data: files, isLoading } = useQuery({
      queryKey: ['files', currentPath, sortBy, sortOrder, searchTerm, page, pageSize],
      queryFn: () =>
        config.fetchFiles({
          path: currentPath,
          search: searchTerm,
          sortBy,
          sortOrder,
          page,
          pageSize,
        }),
    });

    // Delete mutation
    const deleteMutation = useMutation({
      mutationFn: async (paths: string[]) => {
        if (!config.onDelete) {
          throw new Error('Delete functionality not configured');
        }
        await config.onDelete(paths);
      },
      onSuccess: () => {
        queryClient.invalidateQueries({ queryKey: ['files'] });
        setSelectedFiles([]);
        setDeleteConfirmDialog({ isOpen: false, filesToDelete: [] });
        toast.success('Files deleted successfully');
      },
      onError: (error: unknown) => {
        toast.error((error as Error)?.message || 'Failed to delete files');
      },
    });

    // Download mutation
    const downloadMutation = useMutation({
      mutationFn: async (path: string) => {
        if (config.onDownload) {
          await config.onDownload(path);
        }
      },
      onError: (error: unknown) => {
        toast.error((error as Error)?.message || 'Failed to download file');
      },
    });

    const confirmDelete = async () => {
      const pathsToDelete = deleteConfirmDialog.filesToDelete.map((f) => f.path);
      await deleteMutation.mutateAsync(pathsToDelete);
    };

    const handleDeleteSelected = async () => {
      await deleteMutation.mutateAsync(selectedFiles);
    };

    return (
      <div
        ref={ref}
        className={`flex flex-col ${isModal ? 'h-full' : 'space-y-4'}`}
      >
        {/* Toolbar - Search, Sort and View Mode Controls */}
        <div className="flex flex-col lg:flex-row items-stretch lg:items-center gap-3 pt-2">
          {/* Search bar - full width on mobile/tablet, flex-1 on desktop */}
          <div className="flex-1 min-w-0 max-w-none">
            <label htmlFor="file-search" className="sr-only">
              Search files
            </label>
            <Input
              id="file-search"
              type="text"
              placeholder="Search by filename..."
              aria-label="Search files"
              value={searchTerm}
              onChange={(e) => {
                setSearchTerm(e.target.value);
                setPage(1);
              }}
            />
          </div>

          {/* Right-side controls - Sort, View Mode, Buttons */}
          <div className="flex items-center gap-2 flex-wrap lg:flex-nowrap justify-end">
            {/* Sort dropdown - only for grid view (list/explorer handle sorting via column headers) */}
            {viewMode === 'grid' && (
              <>
                <label htmlFor="sort-by" className="text-sm text-pf-text-secondary whitespace-nowrap">
                  Sort:
                </label>
                <Select
                  id="sort-by"
                  aria-label="Sort files by"
                  value={sortBy}
                  onChange={(e) => {
                    setSortBy(e.target.value);
                    setPage(1);
                  }}
                  className="w-32"
                >
                  {(config.sortOptions || [
                    { value: 'name', label: 'Name' },
                    { value: 'size', label: 'Size' },
                    { value: 'date', label: 'Date' },
                  ]).map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </Select>

                {/* Sort direction toggle */}
                <Button
                  type="button"
                  onClick={() => {
                    setSortOrder(sortOrder === 'asc' ? 'desc' : 'asc');
                    setPage(1);
                  }}
                  variant="secondary"
                  size="sm"
                  title={sortOrder === 'asc' ? 'Click to sort descending' : 'Click to sort ascending'}
                >
                  {sortOrder === 'asc' ? '↑' : '↓'}
                </Button>
              </>
            )}


            {/* Delete button */}
            {selectedFiles.length > 0 && config.canDelete && (
              <Button
                type="button"
                onClick={handleDeleteSelected}
                disabled={deleteMutation.isPending}
                variant="danger"
                size="sm"
              >
                Delete ({selectedFiles.length})
              </Button>
            )}

            {/* Extra toolbar buttons (models-specific: Tag, Filter, etc.) */}
            <div className="flex items-center gap-2 flex-shrink-0">
              {config.extraToolbarButtons}
            </div>

            {/* View mode toggle */}
            <FileBrowserViewModeToggle viewMode={viewMode} onViewModeChange={setViewMode} />
          </div>
        </div>

        {/* File listing - different views */}
        {isLoading ? (
          <div className="bg-pf-bg-0 rounded-lg shadow p-8 text-center text-pf-text-secondary">
            Loading...
          </div>
        ) : files && files.files && files.files.length > 0 ? (
          viewMode === 'grid' ? (
            <config.gridViewComponent
              files={files.files}
              onNavigate={(path) => {
                setCurrentPath(path);
                setPage(1);
              }}
              onDelete={(file) => {
                setDeleteConfirmDialog({
                  isOpen: true,
                  filesToDelete: [file],
                  fileName: file.fileName,
                });
              }}
              onDownload={config.onDownload ? (path) => downloadMutation.mutate(path) : undefined}
              isDeleting={deleteMutation.isPending}
            />
          ) : (
            <config.explorerViewComponent
              files={files.files}
              isLoading={isLoading}
              selectedFiles={selectedFiles}
              onSelectFile={(path) => {
                setSelectedFiles((prev) =>
                  prev.includes(path) ? prev.filter((p) => p !== path) : [...prev, path]
                );
              }}
              onSelectAll={(fileList) => {
                if (selectedFiles.length === fileList.length) {
                  setSelectedFiles([]);
                } else {
                  setSelectedFiles(fileList.map((f) => f.path));
                }
              }}
              currentPath={currentPath}
              onNavigate={(path) => {
                setCurrentPath(path);
                setPage(1);
              }}
              sortBy={sortBy}
              sortOrder={sortOrder}
              onSort={(newSortBy) => {
                if (newSortBy === sortBy) {
                  setSortOrder(sortOrder === 'asc' ? 'desc' : 'asc');
                } else {
                  setSortBy(newSortBy);
                  setSortOrder('asc');
                }
                setPage(1);
              }}
            />
          )
        ) : (
          <div className="bg-pf-bg-0 rounded-lg shadow p-8 text-center text-pf-text-secondary">
            {searchTerm ? 'No files match your search' : 'No files found'}
          </div>
        )}

        {/* File count and pagination */}
        {files && (
          <div className="flex flex-col gap-2 text-sm text-pf-text-secondary">
            <div>
              {files.totalFiles} files • {config.formatBytes(files.totalSize)}
            </div>
            <div className="flex items-center gap-3">
              <Button
                type="button"
                disabled={page === 1}
                onClick={() => setPage((p) => Math.max(1, p - 1))}
                variant="secondary"
                size="sm"
              >
                Prev
              </Button>
              <span>
                Page {files.page ?? page} of {files.totalPages ?? '?'}
              </span>
              <Button
                type="button"
                disabled={
                  files.totalPages
                    ? page >= (files.totalPages ?? 1)
                    : (files.files?.length ?? 0) < pageSize
                }
                onClick={() => setPage((p) => p + 1)}
                variant="secondary"
                size="sm"
              >
                Next
              </Button>
              <Select
                aria-label="Select page size"
                value={pageSize}
                onChange={(e) => {
                  setPageSize(Number(e.target.value));
                  setPage(1);
                }}
              >
                {[25, 50, 100, 200, 500].map((size) => (
                  <option key={size} value={size}>
                    {size}/page
                  </option>
                ))}
              </Select>
            </div>
          </div>
        )}

        {/* Delete Confirmation */}
        <ConfirmationModal
          isOpen={deleteConfirmDialog.isOpen}
          onCancel={() => setDeleteConfirmDialog({ isOpen: false, filesToDelete: [] })}
          onConfirm={confirmDelete}
          title="Confirm Delete"
          message={
            deleteConfirmDialog.filesToDelete.length === 1
              ? `Are you sure you want to delete "${deleteConfirmDialog.fileName}"?`
              : `Are you sure you want to delete ${deleteConfirmDialog.filesToDelete.length} files?`
          }
          confirmButtonText="Delete"
          cancelButtonText="Cancel"
          isDangerous={true}
        />
      </div>
    );
  }
);

GenericFileBrowser.displayName = 'GenericFileBrowser';
