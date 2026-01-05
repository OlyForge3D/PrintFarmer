import React, { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { 
  ChevronRightIcon,
  FolderIcon,
  DocumentIcon,
  ArrowDownTrayIcon,
  TrashIcon
} from '@heroicons/react/24/outline';
import { UploadIcon } from '@/common/components/icons/MdiIcons';

import { GcodeFile, GetGcodeFilesResponse } from '@/types/api';
import { Button, Checkbox, Input, Select } from '@/common/components/ui';
import { FileBrowserViewModeToggle } from '@/common/components/FileBrowserViewModeToggle';
import { GcodeUploadModal } from '@/common/components/modals/GcodeUploadModal';
import { ExplorerFileBrowser } from '@/features/gcode/components/ExplorerFileBrowser';
import { GcodeFileCard } from '@/features/gcode/components/GcodeFileCard';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { apiClient } from '@/services/api';
import { prefetchFileHash } from '@/features/gcode/hooks/useFileHash';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';

interface FileBrowserProps {
  harvestId?: string;
  printerId?: string;
  initialPath?: string;
  isModal?: boolean;
}

interface UploadItem {
  id: string;
  file: File;
  progress: number;
  status: 'queued' | 'uploading' | 'done' | 'error' | 'cancelled';
  error?: string;
  cancelRequested?: boolean;
  paused?: boolean;
  // For future chunked resume (not yet implemented server-side)
  uploadedBytes?: number;
  isChunked?: boolean;
  finalHash?: string;
  uploadId?: string;
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
  initialPath = '/',
  isModal = false
}) => {
  const { hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const [currentPath, setCurrentPath] = useState(initialPath);
  const [selectedFiles, setSelectedFiles] = useState<GcodeFile[]>([]);
  const { viewMode, setViewMode } = useViewModePreference('printfarmer-gcode-viewmode');
  const [showUploadModal, setShowUploadModal] = useState(false);
  const [sortBy, setSortBy] = useState<'name' | 'size' | 'date'>('name');
  const [sortOrder, setSortOrder] = useState<'asc' | 'desc'>('asc');
  const [searchTerm, setSearchTerm] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const [uploadQueue, setUploadQueue] = useState<UploadItem[]>([]);
  // Restore persisted queue including resumable chunk sessions
  useEffect(() => {
    const raw = localStorage.getItem('pf.uploadQueue');
    if (raw) {
      try {
        const parsed: { id: string; fileName: string; fileSize: number; fileType: string; progress: number; status: UploadItem['status']; error?: string; uploadId?: string; uploadedBytes?: number; isChunked?: boolean; finalHash?: string; paused?: boolean }[] = JSON.parse(raw);
        const rebuilt: UploadItem[] = parsed.map(p => {
          const f = new File([], p.fileName, { type: p.fileType, lastModified: Date.now() });
          const resumable = p.isChunked && p.uploadId && p.status !== 'done' && p.status !== 'cancelled';
          return {
            id: p.id,
            file: f,
            progress: p.progress,
            status: resumable ? 'queued' : (p.status === 'done' ? 'done' : 'cancelled'),
            error: p.error,
            cancelRequested: !resumable && p.status !== 'done',
            uploadId: p.uploadId,
            uploadedBytes: p.uploadedBytes,
            isChunked: p.isChunked,
            finalHash: p.finalHash,
            paused: p.paused
          };
        });
        setUploadQueue(rebuilt);
      } catch {
        // Ignore localStorage parsing errors
      }
    }
  }, []);

  // Persist queue metadata on change
  useEffect(() => {
    const serializable = uploadQueue.map(u => ({
      id: u.id,
      fileName: u.file.name,
      fileSize: u.file.size,
      fileType: u.file.type,
      progress: u.progress,
      status: u.status,
      error: u.error,
      uploadId: u.uploadId,
      uploadedBytes: u.uploadedBytes,
      isChunked: u.isChunked,
      finalHash: u.finalHash,
      paused: u.paused
    }));
    try { localStorage.setItem('pf.uploadQueue', JSON.stringify(serializable)); } catch {
      // Ignore localStorage write errors
    }
  }, [uploadQueue]);
  const currentXhrRef = useRef<XMLHttpRequest | null>(null);
  const abortAllRef = useRef(false);

  const { data: files, isLoading } = useQuery<GetGcodeFilesResponse>({
    queryKey: ['gcode-files', currentPath, harvestId, printerId, sortBy, sortOrder, searchTerm, page, pageSize],
    queryFn: async () => {
      // At root path, show all files from library instead of empty folder
      if (currentPath === '/' && !harvestId && !printerId) {
        const libraryFiles = await apiClient.queryGcodeLibrary(searchTerm);
        // Map library files to file browser structure (minimal transformation - just add missing fields)
        const files: GcodeFile[] = libraryFiles.map(f => ({
          ...f,
          path: `/${f.fileName}`,
          isDirectory: false,
          harvestOperationId: undefined
        }));
        // Calculate total size for library files
        const totalSize = libraryFiles.reduce((sum, f) => sum + (f.fileSize || 0), 0);
        return {
          files,
          totalFiles: files.length,
          totalSize,
          currentPath: '/',
          parentPath: null,
          subfolders: [],
          page: 1,
          totalPages: 1
        };
      }
      // Otherwise use hierarchical file browser
      return apiClient.getGcodeFilesWithFilter({
        path: currentPath,
        harvestId,
        printerId,
        sortBy,
        sortOrder,
        search: searchTerm,
        page,
        pageSize
      });
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (files: GcodeFile[]) => apiClient.deleteGcodeFiles(files.map(f => f.id)),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['gcode-files'] });
      setSelectedFiles([]);
      toast.success('Files deleted successfully');
    },
    onError: () => {
      toast.error('Failed to delete files');
    }
  });

  const uploadMutation = useMutation({
    mutationFn: async (files: File[]) => {
  const results = { succeeded: 0, failed: 0, cancelled: 0 };
  const CHUNK_THRESHOLD = 8 * 1024 * 1024; // 8MB threshold for chunked strategy
  const queueItems: UploadItem[] = files.map(f => ({ id: (crypto?.randomUUID?.() || Math.random().toString(36).slice(2)), file: f, progress: 0, status: 'queued', cancelRequested: false, paused: false, isChunked: f.size >= CHUNK_THRESHOLD }));
      setUploadQueue(prev => [...prev, ...queueItems]);
      const CHUNK_SIZE = 1 * 1024 * 1024; // 1MB slices
      const apiBase = getApiBaseUrl();

      const chunkUpload = async (item: UploadItem): Promise<void> => {
        let uploadId = item.uploadId;
        let offset = item.uploadedBytes || 0;
        if (!uploadId) {
          const initResp = await fetch(`${apiBase}/gcode-files/chunk/init`, {
            method: 'POST',
            headers: { 
              'Content-Type': 'application/json',
              ...getAuthHeaders()
            },
            body: JSON.stringify({ fileName: item.file.name, size: item.file.size, path: currentPath })
          });
          if (!initResp.ok) {
            throw new Error((await initResp.text()) || 'Chunk init failed');
          }
          const initData = await initResp.json() as { uploadId: string; recommendedChunkSize: number; totalSize: number; uploadedBytes: number; };
          uploadId = initData.uploadId;
          offset = initData.uploadedBytes || 0;
          item.uploadId = uploadId;
          item.uploadedBytes = offset;
          setUploadQueue(q => [...q]);
        } else {
          // Rehydrate from server
          try {
            const stResp = await fetch(`${apiBase}/gcode-files/chunk/${uploadId}`, {
              headers: getAuthHeaders()
            });
            if (stResp.ok) {
              const st = await stResp.json();
              offset = st.uploadedBytes || 0;
              item.uploadedBytes = offset;
              item.progress = Math.round((offset / item.file.size) * 100);
              item.paused = !!st.paused;
              if (st.completed) {
                item.status = 'done';
                item.finalHash = st.finalHash;
                setUploadQueue(q => [...q]);
                return;
              }
              setUploadQueue(q => [...q]);
            }
          } catch {
            // Ignore status check errors
          }
        }
        while (offset < item.file.size) {
          if (item.cancelRequested) {
            // cancel on server
            try { fetch(`${apiBase}/gcode-files/chunk/${uploadId}`, { method: 'DELETE', headers: getAuthHeaders() }); } catch {
              // Ignore cleanup errors
            }
            throw new Error('Cancelled');
          }
          if (item.paused) { await new Promise(r => setTimeout(r, 250)); continue; }
          const slice = item.file.slice(offset, Math.min(offset + CHUNK_SIZE, item.file.size));
          const putResp = await fetch(`${apiBase}/gcode-files/chunk/${uploadId}?offset=${offset}`, {
            method: 'PUT',
            headers: { 
              'Content-Type': 'application/octet-stream',
              ...getAuthHeaders()
            },
            body: slice
          });
          if (!putResp.ok) {
            if (putResp.status === 409) {
              // offset mismatch - abort
              throw new Error('Offset mismatch');
            }
            if (putResp.status === 423) {
              // Server paused the upload
              try { await putResp.json(); item.paused = true; } catch { item.paused = true; }
              setUploadQueue(q => [...q]);
              await new Promise(r => setTimeout(r, 1000));
              continue;
            }
            throw new Error((await putResp.text()) || 'Chunk upload failed');
          }
          const statusJson = await putResp.json().catch(() => null) as { isComplete?: boolean; finalHash?: string; paused?: boolean; completed?: boolean } | null;
          offset += slice.size;
          item.uploadedBytes = offset;
          item.progress = Math.round((offset / item.file.size) * 100);
          item.paused = !!statusJson?.paused;
          setUploadQueue(q => [...q]);
          if (statusJson?.completed) {
            if (statusJson?.finalHash) {
              item.finalHash = statusJson.finalHash;
            }
          }
        }
      };
      for (let i = 0; i < queueItems.length; i++) {
        if (abortAllRef.current) break;
        const item = queueItems[i];
        // If user cancelled before we started
        if (item.cancelRequested) {
            item.status = 'cancelled';
            results.cancelled++;
            setUploadQueue(q => [...q]);
            continue;
        }
  if (item.paused) { continue; }
  item.status = 'uploading';
        setUploadQueue(q => [...q]);
        try {
          if (item.file.size >= CHUNK_THRESHOLD) {
            await chunkUpload(item);
            item.progress = 100;
          } else {
            await new Promise<void>((resolve, reject) => {
              const form = new FormData();
              form.append('file', item.file);
              const xhr = new XMLHttpRequest();
              currentXhrRef.current = xhr;
              xhr.upload.onprogress = (e) => {
                if (e.lengthComputable) {
                  item.progress = Math.round((e.loaded / e.total) * 100);
                  setUploadQueue(q => [...q]);
                }
              };
              xhr.onload = () => {
                if (xhr.status >= 200 && xhr.status < 300) {
                  item.progress = 100;
                  resolve();
                } else if (xhr.status === 0) {
                  reject(new Error('Cancelled'));
                } else {
                  reject(new Error(xhr.responseText || `HTTP ${xhr.status}`));
                }
              };
              xhr.onerror = () => reject(new Error('Network error'));
              xhr.onabort = () => reject(new Error('Cancelled'));
              xhr.open('POST', `${apiBase}/gcode-files/upload?path=${encodeURIComponent(currentPath)}`);
              const headers = getAuthHeaders();
              Object.entries(headers).forEach(([key, value]) => {
                xhr.setRequestHeader(key, value as string);
              });
              xhr.send(form);
            });
          }
          if (item.cancelRequested) {
            item.status = 'cancelled';
            results.cancelled++;
          } else {
            item.status = 'done';
            if (item.finalHash) {
              toast.success(`${item.file.name} uploaded (hash ${item.finalHash.slice(0,8)}…)`);
            }
            results.succeeded++;
          }
        } catch (err: unknown) {
          if (item.cancelRequested || /cancelled/i.test((err as Error)?.message || '')) {
            item.status = 'cancelled';
            results.cancelled++;
          } else {
            item.status = 'error';
            item.error = (err as Error)?.message || 'Failed';
            results.failed++;
          }
        }
        setUploadQueue(q => [...q]);
        currentXhrRef.current = null;
      }
      return results;
    },
    onSuccess: (res) => {
      queryClient.invalidateQueries({ queryKey: ['gcode-files'] });
      // Also invalidate models list since gcode uploads may be associated with models
      queryClient.invalidateQueries({ queryKey: ['models-search'] });
      abortAllRef.current = false; // reset global abort flag
      const parts: string[] = [];
      if (res.succeeded) parts.push(`${res.succeeded} ok`);
      if (res.failed) parts.push(`${res.failed} failed`);
      if (res.cancelled) parts.push(`${res.cancelled} cancelled`);
      toast.success(`Uploads: ${parts.join(', ') || 'none'}`);
      // Upload queue remains visible until user manually clears it
    },
    onError: (err: unknown) => {
      const msg = err instanceof Error ? err.message : 'Upload failed';
      toast.error(msg);
    }
  });

  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const createFolderMutation = useMutation({
    mutationFn: async (name: string) => {
      const resp = await fetch(`${getApiBaseUrl()}/gcode-files/mkdir?path=${encodeURIComponent(currentPath)}&name=${encodeURIComponent(name)}`, { 
        method: 'POST',
        headers: getAuthHeaders()
      });
      if (!resp.ok) {
        const text = await resp.text();
        throw new Error(text || 'Failed to create directory');
      }
      return resp.json();
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['gcode-files'] });
      toast.success('Directory created');
    },
    onError: (err: unknown) => {
      const msg = err instanceof Error ? err.message : 'Failed to create directory';
      toast.error(msg);
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
      setSelectedFiles(files?.files?.filter((f: GcodeFile) => !f.isDirectory) || []);
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
    <div className={`flex flex-col ${isModal ? 'h-full' : 'space-y-4'}`}>
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center space-x-4">
          {/* Breadcrumbs */}
          <nav className="flex items-center space-x-2 text-sm">
            <Button
              type="button"
              onClick={() => setCurrentPath('/')}
              variant="subtle"
              size="sm"
            >
              Root
            </Button>
            
            {breadcrumbs.map((segment, index) => (
              <React.Fragment key={index}>
                <ChevronRightIcon className="w-4 h-4 text-pf-text-tertiary" />
                <Button
                  type="button"
                  onClick={() => { setCurrentPath('/' + breadcrumbs.slice(0, index + 1).join('/')); setPage(1);} }
                  variant="subtle"
                  size="sm"
                >
                  {segment}
                </Button>
              </React.Fragment>
            ))}
          </nav>
        </div>
        <div className="flex items-center space-x-2">
          {selectedFiles.length > 0 && hasPermission('gcode_harvest', 'delete') && (
            <Button
              type="button"
              onClick={handleDeleteSelected}
              disabled={deleteMutation.isPending}
              variant="danger"
              size="sm"
            >
              Delete Selected ({selectedFiles.length})
            </Button>
          )}
          {selectedFiles.length > 1 && (
            <Button
              type="button"
              onClick={async () => {
                // Bulk hash compare: compute & group by hash to find duplicates
                const candidatePaths = files?.files?.filter(f => selectedFiles.includes(f.path) && !f.isDirectory && /\.(gcode|bgcode)$/i.test(f.fileName)).map(f => f.path) || [];
                if (candidatePaths.length < 2) { toast.info('Select at least two files'); return; }
                try {
                  // Prefetch all hashes
                  await Promise.all(candidatePaths.map(p => prefetchFileHash(queryClient, p, 'sha256')));
                  const groups: Record<string, string[]> = {};
                  for (const p of candidatePaths) {
                    const data = queryClient.getQueryData<{ fileName: string; hash: string }>(['gcode-file-hash', p, 'sha256']);
                    if (data?.hash) {
                      groups[data.hash] = groups[data.hash] || [];
                      groups[data.hash].push(p);
                    }
                  }
                  const dupes = Object.entries(groups).filter(([, arr]) => arr.length > 1);
                  if (dupes.length === 0) {
                    toast.success('No duplicates found');
                  } else {
                    const summary = dupes.map(([hash, arr]) => `${hash.slice(0,8)}… (${arr.length})`).join(', ');
                    toast.info(`Duplicates: ${summary}`);
                  }
                } catch (e: unknown) {
                  toast.error((e as Error)?.message || 'Duplicate scan failed');
                }
              }}
              variant="secondary"
              size="sm"
            >
              Find Duplicates ({selectedFiles.length})
            </Button>
          )}
          {hasPermission('gcode_harvest', 'create') && (
            <Button
              type="button"
              onClick={() => setShowUploadModal(true)}
              variant="secondary"
              size="sm"
            >
                <UploadIcon className="w-4 h-4 mr-1" />
            </Button>
          )}
          <FileBrowserViewModeToggle 
            viewMode={viewMode}
            onViewModeChange={setViewMode}
          />
        </div>
      </div>
      {/* Search and filters */}
      <div className="flex items-center space-x-4">
        <div className="flex-1 max-w-md">
          <label htmlFor="file-search" className="sr-only">Search files</label>
          <Input
            id="file-search"
            type="text"
            placeholder="Search by filename or path..."
            aria-label="Search files by filename or path"
            title="Search by filename or full file path"
            value={searchTerm}
            onChange={(e) => { setSearchTerm(e.target.value); setPage(1);} }
          />
        </div>
        
        <label htmlFor="sort-by" className="sr-only">Sort by</label>
        <Select
          id="sort-by"
          aria-label="Sort files by"
          value={sortBy}
          onChange={(e) => { setSortBy(e.target.value as 'name' | 'size' | 'date'); setPage(1);} }
        >
          <option value="name">Sort by Name</option>
          <option value="size">Sort by Size</option>
          <option value="date">Sort by Date</option>
        </Select>
        
        <Button
          type="button"
          onClick={() => { setSortOrder(sortOrder === 'asc' ? 'desc' : 'asc'); setPage(1);} }
          variant="secondary"
          size="sm"
        >
          {sortOrder === 'asc' ? '↑' : '↓'}
        </Button>
      </div>
      {/* Upload modal and file listing - scrollable container in modal mode */}
      <GcodeUploadModal
        isOpen={showUploadModal}
        onClose={() => setShowUploadModal(false)}
        onFilesSelected={(files) => uploadMutation.mutate(files)}
        harvestId={harvestId}
        printerId={printerId}
      />
      <div className={`${isModal ? 'flex-1 overflow-y-auto' : ''}`}>
      {/* Explorer view is always shown - it manages its own state and API calls */}
      {viewMode === 'explorer' ? (
        <div className="bg-pf-bg-1 rounded-lg border border-pf-border h-full flex flex-col overflow-hidden">
          <ExplorerFileBrowser endpoint="gcode" />
        </div>
      ) : isLoading ? (
        <div className="space-y-2">
          {[...Array(5)].map((_, i) => (
            <div key={i} className="h-16 bg-pf-bg-1 rounded animate-pulse" />
          ))}
        </div>
      ) : files && files.files && files.files.length > 0 ? (
        viewMode === 'grid' ? (
          // Grid view - card layout
          <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-3 overflow-y-auto">
            {files.files?.map((file: GcodeFile) => (
              <GcodeFileCard
                key={file.path}
                file={file}
                onNavigate={(path) => {
                  setCurrentPath(path);
                  setPage(1);
                }}
                onDownload={(path) => downloadMutation.mutate(path)}
                onDelete={(path) => {
                  if (confirm(`Delete ${file.fileName}?`)) {
                    deleteMutation.mutate([file]);
                  }
                }}
                isDeleting={deleteMutation.isPending}
              />
            ))}
          </div>
        ) : (
          // List view (default)
          <div className="bg-pf-bg-1 rounded-lg shadow-lg border border-pf-border overflow-hidden">
            <table className="w-full">
              <thead>
                <tr className="border-b border-pf-border bg-pf-bg-2">
                  <th className="px-4 py-3 text-left">
                    <Checkbox
                      title="Select all files"
                      aria-label="Select all files"
                      checked={selectedFiles.length === (files?.files?.length ?? 0) && (files?.files?.length ?? 0) > 0}
                      onChange={handleSelectAll}
                    />
                  </th>
                  <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary">Thumbnail / Name</th>
                  <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary w-24">Size</th>
                  <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary w-24">Nozzle</th>
                  <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary w-32">Material</th>
                  <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary w-40">Modified</th>
                  <th className="px-4 py-3 text-right text-sm font-medium text-pf-text-primary">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-pf-border">
                {files.files?.map((file: GcodeFile) => (
                  <tr key={file.path} className="hover:bg-pf-bg-2 transition-colors">
                    <td className="px-4 py-3">
                      <Checkbox
                        checked={selectedFiles.includes(file.path)}
                        onChange={(e) => {
                          if (e.target.checked) {
                            setSelectedFiles(prev => [...prev, file.path]);
                          } else {
                            setSelectedFiles(prev => prev.filter(p => p !== file.path));
                          }
                        }}
                        title={`Select ${file.fileName}`}
                        aria-label={`Select ${file.fileName}`}
                      />
                    </td>
                    <td className="px-4 py-3">
                      <div className="flex items-center gap-3">
                        <div className="relative flex-shrink-0">
                          {!file.isDirectory && file.thumbnailUrl ? (
                            <img
                              src={file.thumbnailUrl}
                              alt={file.fileName}
                              className="w-10 h-10 rounded object-cover border-2 border-pf-border"
                              onError={(e) => {
                                e.currentTarget.src = 'data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iNDgiIGhlaWdodD0iNDgiIHZpZXdCb3g9IjAgMCA0OCA0OCIgZmlsbD0ibm9uZSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj48cmVjdCB3aWR0aD0iNDgiIGhlaWdodD0iNDgiIGZpbGw9IiNFNUU3RUIiLz48cmVjdCB4PSI4IiB5PSI4IiB3aWR0aD0iMzIiIGhlaWdodD0iMzIiIHN0cm9rZT0iIzk1OTdiMCIgc3Ryb2tlLXdpZHRoPSIyIiBmaWxsPSJub25lIi8+PGNpcmNsZSBjeD0iMjQiIGN5PSIyNCIgcj0iMiIgZmlsbD0iIzk1OTdiMCIvPjwvc3ZnPg=='
                              }}
                            />
                          ) : (
                            <div className="w-10 h-10 rounded border-2 border-pf-border bg-pf-bg-2 flex items-center justify-center">
                              {file.isDirectory ? (
                                <FolderIcon className="w-5 h-5 text-pf-accent" />
                              ) : (
                                <DocumentIcon className="w-5 h-5 text-pf-text-tertiary" />
                              )}
                            </div>
                          )}
                        </div>
                        <div
                          className="cursor-pointer hover:text-pf-accent"
                          onClick={() => {
                            if (file.isDirectory) {
                              setCurrentPath(file.path);
                              setPage(1);
                            }
                          }}
                        >
                          <div className="font-medium text-pf-text-primary">{file.name}</div>
                          {file.extractedPrinterModel && (
                            <div className="text-xs text-pf-text-tertiary">{file.extractedPrinterModel}</div>
                          )}
                        </div>
                      </div>
                    </td>
                    <td className="px-4 py-3 text-sm text-pf-text-secondary">
                      {!file.isDirectory ? formatBytes(file.fileSize) : '-'}
                    </td>
                    <td className="px-4 py-3 text-sm text-pf-text-secondary">
                      {!file.isDirectory && file.extractedNozzleDiameter ? `${file.extractedNozzleDiameter}mm` : '-'}
                    </td>
                    <td className="px-4 py-3 text-sm text-pf-text-secondary">
                      {!file.isDirectory ? (file.extractedMaterial || '-') : '-'}
                    </td>
                    <td className="px-4 py-3 text-sm text-pf-text-secondary">
                      {file.uploadedAt ? new Date(file.uploadedAt).toLocaleDateString() : '-'}
                    </td>
                    <td className="px-4 py-3 text-right">
                      <div className="flex justify-end gap-2">
                        {!file.isDirectory && (
                          <>
                            <Button
                              onClick={() => downloadMutation.mutate(file.path)}
                              disabled={downloadMutation.isPending}
                              variant="secondary"
                              size="sm"
                              title="Download File"
                            >
                              <ArrowDownTrayIcon className="w-4 h-4" />
                            </Button>
                            <Button
                              onClick={() => {
                                if (confirm(`Delete ${file.fileName}?`)) {
                                  deleteMutation.mutate([file]);
                                }
                              }}
                              disabled={deleteMutation.isPending}
                              variant="danger"
                              size="sm"
                              title="Delete File"
                            >
                              <TrashIcon className="w-4 h-4" />
                            </Button>
                          </>
                        )}
                        {file.isDirectory && (
                          <Button
                            onClick={() => {
                              if (confirm(`Delete ${file.fileName}?`)) {
                                deleteMutation.mutate([file]);
                              }
                            }}
                            disabled={deleteMutation.isPending}
                            variant="danger"
                            size="sm"
                            title="Delete Folder"
                          >
                            <TrashIcon className="w-4 h-4" />
                          </Button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )
      ) : (
        <div className="bg-pf-bg-0 rounded-lg shadow p-8 text-center text-pf-text-secondary">
          {searchTerm ? 'No files match your search' : 'No files found'}
        </div>
      )}
      {/* File count and size */}
      {files && (
  <div className="flex flex-col gap-2 text-sm text-pf-text-secondary">
          <div>
            {files.totalFiles} files • {formatBytes(files.totalSize)}
          </div>
          {/* Pagination controls */}
          <div className="flex items-center gap-3">
            <Button
              type="button"
              disabled={page === 1}
              onClick={() => setPage(p => Math.max(1, p - 1))}
              variant="secondary"
              size="sm"
            >
              Prev
            </Button>
            <span>Page {(files.page ?? page)} of {(files.totalPages ?? '?')}</span>
            <Button
              type="button"
              disabled={files.totalPages ? page >= (files.totalPages ?? 1) : ((files.files?.length ?? 0) < pageSize)}
              onClick={() => setPage(p => p + 1)}
              variant="secondary"
              size="sm"
            >
              Next
            </Button>
            <Select
              aria-label="Select page size"
              value={pageSize}
              onChange={e => { setPageSize(Number(e.target.value)); setPage(1);} }
            >
              {[25,50,100,200,500].map(size => <option key={size} value={size}>{size}/page</option>)}
            </Select>
          </div>
        </div>
      )}
      </div>
    </div>
  );
};