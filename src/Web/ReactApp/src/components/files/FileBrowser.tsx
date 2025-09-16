import React, { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { ChevronRightIcon } from '@heroicons/react/24/outline';

import { GcodeFile, GetGcodeFilesResponse, GcodeUploadSettings } from '@/types/api';
import styles from './FileBrowser.module.css';
import { useAuth } from '@/contexts/AuthHooks';
import { apiClient } from '@/services/api';
import { FileRow } from './FileRow';
import { prefetchFileHash } from '@/hooks/useFileHash';

interface FileBrowserProps {
  harvestId?: string;
  printerId?: string;
  initialPath?: string;
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
  const [showSettings, setShowSettings] = useState(false);
  const [settings, setSettings] = useState<GcodeUploadSettings | null>(null);
  const [extensionsInput, setExtensionsInput] = useState('');

  useEffect(() => {
    if (showSettings) {
      apiClient.getGcodeUploadSettings().then(s => { setSettings(s); setExtensionsInput(s.allowedExtensions.join(', ')); }).catch(() => {});
    }
  }, [showSettings]);

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

  const uploadMutation = useMutation({
    mutationFn: async (files: File[]) => {
  const results = { succeeded: 0, failed: 0, cancelled: 0 };
  const CHUNK_THRESHOLD = 8 * 1024 * 1024; // 8MB threshold for chunked strategy
  const queueItems: UploadItem[] = files.map(f => ({ id: (crypto?.randomUUID?.() || Math.random().toString(36).slice(2)), file: f, progress: 0, status: 'queued', cancelRequested: false, paused: false, isChunked: f.size >= CHUNK_THRESHOLD }));
      setUploadQueue(prev => [...prev, ...queueItems]);
      const CHUNK_SIZE = 1 * 1024 * 1024; // 1MB slices
      const apiBase = import.meta.env.VITE_API_BASE_URL || '/api';

      const chunkUpload = async (item: UploadItem): Promise<void> => {
        let uploadId = item.uploadId;
        let offset = item.uploadedBytes || 0;
        if (!uploadId) {
          const initResp = await fetch(`${apiBase}/gcode-files/chunk/init`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ fileName: item.file.name, size: item.file.size, path: currentPath })
          });
          if (!initResp.ok) {
            throw new Error(await initResp.text() || 'Chunk init failed');
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
            const stResp = await fetch(`${apiBase}/gcode-files/chunk/${uploadId}`);
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
            try { fetch(`${apiBase}/gcode-files/chunk/${uploadId}`, { method: 'DELETE' }); } catch {
              // Ignore cleanup errors
            }
            throw new Error('Cancelled');
          }
          if (item.paused) { await new Promise(r => setTimeout(r, 250)); continue; }
          const slice = item.file.slice(offset, Math.min(offset + CHUNK_SIZE, item.file.size));
          const putResp = await fetch(`${apiBase}/gcode-files/chunk/${uploadId}?offset=${offset}`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/octet-stream' },
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
            throw new Error(await putResp.text() || 'Chunk upload failed');
          }
          const statusJson = await putResp.json().catch(() => null) as any;
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
        } catch (err: any) {
          if (item.cancelRequested || /cancelled/i.test(err?.message || '')) {
            item.status = 'cancelled';
            results.cancelled++;
          } else {
            item.status = 'error';
            item.error = err?.message || 'Failed';
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
      abortAllRef.current = false; // reset global abort flag
      const parts: string[] = [];
      if (res.succeeded) parts.push(`${res.succeeded} ok`);
      if (res.failed) parts.push(`${res.failed} failed`);
      if (res.cancelled) parts.push(`${res.cancelled} cancelled`);
      toast.success(`Uploads: ${parts.join(', ') || 'none'}`);
      setTimeout(() => setUploadQueue(q => q.filter(i => i.status === 'uploading')), 4000);
    },
    onError: (err: unknown) => {
      const msg = err instanceof Error ? err.message : 'Upload failed';
      toast.error(msg);
    }
  });

  const mkdirMutation = useMutation({
    mutationFn: async (name: string) => {
      const resp = await fetch(`${import.meta.env.VITE_API_BASE_URL || '/api'}/gcode-files/mkdir?path=${encodeURIComponent(currentPath)}&name=${encodeURIComponent(name)}`, { method: 'POST' });
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
              className="text-pf-link hover:text-pf-accent"
            >
              Root
            </button>
            
            {breadcrumbs.map((segment, index) => (
              <React.Fragment key={index}>
                <ChevronRightIcon className="w-4 h-4 text-pf-text-tertiary" />
                <button
                  onClick={() => { setCurrentPath('/' + breadcrumbs.slice(0, index + 1).join('/')); setPage(1);} }
                  className="text-pf-link hover:text-pf-accent"
                >
                  {segment}
                </button>
              </React.Fragment>
            ))}
          </nav>
        </div>
        <div className="flex items-center space-x-2">
          {selectedFiles.length > 0 && hasPermission('gcode_harvest', 'delete') && (
            <button
              onClick={handleDeleteSelected}
              disabled={deleteMutation.isPending}
              className="px-3 py-1 bg-red-600 text-white text-sm rounded hover:bg-red-700 disabled:opacity-50"
            >Delete Selected ({selectedFiles.length})</button>
          )}
          {selectedFiles.length > 1 && (
            <button
              onClick={async () => {
                // Bulk hash compare: compute & group by hash to find duplicates
                const candidatePaths = files?.files?.filter(f => selectedFiles.includes(f.path) && !f.isDirectory && /\.(gcode|bgcode)$/i.test(f.name)).map(f => f.path) || [];
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
                } catch (e:any) {
                  toast.error(e?.message || 'Duplicate scan failed');
                }
              }}
              className="px-3 py-1 bg-purple-600 text-white text-sm rounded hover:bg-purple-700 disabled:opacity-50"
            >Find Duplicates ({selectedFiles.length})</button>
          )}
          {hasPermission('gcode_harvest', 'create') && (
            <button
              onClick={() => {
                const name = prompt('New directory name');
                if (name) mkdirMutation.mutate(name);
              }}
              className="px-3 py-1 bg-gray-200 text-sm rounded hover:bg-gray-300 disabled:opacity-50"
              disabled={mkdirMutation.isPending}
            >New Folder</button>
          )}
          {hasPermission('gcode_harvest', 'update') && (
            <button
              onClick={() => setShowSettings(s => !s)}
              className="px-3 py-1 bg-pf-bg-2 text-sm rounded hover:bg-pf-bg-1 text-pf-text-primary border border-pf-border"
            >{showSettings ? 'Close Settings' : 'Settings'}</button>
          )}
          <div className="flex border border-pf-border rounded">
            <button
              onClick={() => setViewMode('list')}
              className={`px-3 py-1 text-sm ${viewMode === 'list' ? 'bg-pf-accent text-white' : 'text-pf-text-primary bg-pf-bg-1'}`}
            >List</button>
            <button
              onClick={() => setViewMode('grid')}
              className={`px-3 py-1 text-sm ${viewMode === 'grid' ? 'bg-pf-accent text-white' : 'text-pf-text-primary bg-pf-bg-1'}`}
            >Grid</button>
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
            className="w-full px-3 py-2 border border-pf-border rounded-md bg-pf-bg-0 text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent"
          />
        </div>
        
        <label htmlFor="sort-by" className="sr-only">Sort by</label>
        <select
          id="sort-by"
          aria-label="Sort files by"
          value={sortBy}
          onChange={(e) => { setSortBy(e.target.value as 'name' | 'size' | 'date'); setPage(1);} }
          className="px-3 py-2 border border-pf-border rounded-md bg-pf-bg-0 text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent"
        >
          <option value="name">Sort by Name</option>
          <option value="size">Sort by Size</option>
          <option value="date">Sort by Date</option>
        </select>
        
        <button
          onClick={() => { setSortOrder(sortOrder === 'asc' ? 'desc' : 'asc'); setPage(1);} }
          className="px-3 py-2 border border-pf-border rounded-md hover:bg-pf-bg-1 text-pf-text-primary bg-pf-bg-0"
        >
          {sortOrder === 'asc' ? '↑' : '↓'}
        </button>
      </div>

      {/* Drag & drop + click upload area */}
      {hasPermission('gcode_harvest', 'create') && (
        <div className="space-y-2">
          <div
            onDragOver={(e) => { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }}
            onDrop={(e) => {
              e.preventDefault();
              if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
                const accepted = Array.from(e.dataTransfer.files).filter(f => /\.(gcode|bgcode)$/i.test(f.name));
                if (accepted.length === 0) {
                  toast.error('No valid .gcode or .bgcode files');
                  return;
                }
                uploadMutation.mutate(accepted);
              }
            }}
            className="border-2 border-dashed border-pf-border rounded p-6 text-center text-sm text-pf-text-secondary hover:border-pf-accent transition-colors cursor-pointer"
            onClick={() => {
              const input = document.createElement('input');
              input.type = 'file';
              input.multiple = true;
              input.accept = (settings?.allowedExtensions || ['.gcode','.bgcode']).join(',');
              input.onchange = () => {
                if (input.files) {
                  const allowed = settings?.allowedExtensions || ['.gcode','.bgcode'];
                  const re = new RegExp(`(${allowed.map(e => e.replace('.', '\\.')).join('|')})$`, 'i');
                  const files = Array.from(input.files).filter(f => re.test(f.name));
                  if (files.length > 0) uploadMutation.mutate(files);
                }
              };
              input.click();
            }}
          >
            {uploadMutation.isPending ? 'Uploading...' : `Click or drag & drop files (${(settings?.allowedExtensions || ['.gcode','.bgcode']).join(', ')})`}
          </div>
          <p className="text-xs text-pf-text-tertiary">Supports multi-file upload. New files will auto-rename on collision.</p>
          {uploadQueue.length > 0 && (
            <div className="space-y-1 border border-pf-border rounded p-2 bg-pf-bg-1">
              <div className="flex items-center justify-between mb-1">
                <span className="text-xs font-medium text-pf-text-secondary">Uploads</span>
                <div className="flex gap-2">
                  <button
                    className="text-xs px-2 py-0.5 border rounded hover:bg-gray-50"
                    onClick={() => {
                      abortAllRef.current = true;
                      currentXhrRef.current?.abort();
                      setUploadQueue(q => q.map(it => {
                        if (it.status === 'done' || it.status === 'error' || it.status === 'cancelled') return it;
                        if (it.status === 'uploading') return { ...it, cancelRequested: true };
                        // queued
                        return { ...it, cancelRequested: true, status: 'cancelled' };
                      }));
                    }}
                  >Cancel All</button>
                  <button
                    className="text-xs px-2 py-0.5 border rounded hover:bg-gray-50"
                    onClick={() => setUploadQueue([])}
                  >Clear</button>
                </div>
              </div>
              {uploadQueue.map(item => (
                <div key={item.id} className="flex items-center gap-2 text-[11px]">
                  <span className="w-40 truncate" title={item.file.name}>{item.file.name}</span>
                  <div className={`flex-1 ${styles.progressBarContainer}`}>
                    {(() => {
                      const pct = Math.min(100, Math.max(0, item.progress));
                      const even = Math.round(pct / 2) * 2; // snap to 2%
                      const widthClass = (styles as any)[`w${even}`] || (styles as any).w100 || '';
                      return (
                        <div
                          className={[
                            styles.progressFill,
                            widthClass,
                            item.status === 'error' ? styles.progressError : '',
                            item.status === 'cancelled' ? styles.progressCancelled : ''
                          ].filter(Boolean).join(' ')}
                        />
                      );
                    })()}
                  </div>
                  <span className="w-16 text-right">
                    {item.status === 'uploading' ? `${item.progress}%` : item.status}
                  </span>
                  {item.status === 'error' && item.error && (
                    <button
                      className="text-yellow-600 hover:text-yellow-700"
                      title={item.error + ' - retry'}
                      onClick={() => {
                        // Reset and requeue as queued (will start on next mutate call or manual retry process)
                        setUploadQueue(q => q.map(it => it.id === item.id ? { ...it, status: 'queued', error: undefined, progress: 0, cancelRequested: false } : it));
                        // Kick off single-file upload by invoking mutation with just that file
                        uploadMutation.mutate([item.file]);
                      }}
                    >Retry</button>
                  )}
                  {item.isChunked && (item.status === 'uploading' || item.status === 'queued') && (
                    <button
                      className="text-gray-400 hover:text-orange-600"
                      title={item.paused ? 'Resume upload' : 'Pause upload'}
                      onClick={async () => {
                        if (!item.uploadId) return;
                        const apiBase = import.meta.env.VITE_API_BASE_URL || '/api';
                        try {
                          if (item.paused) {
                            await fetch(`${apiBase}/gcode-files/chunk/${item.uploadId}/resume`, { method: 'POST' });
                            item.paused = false;
                          } else {
                            await fetch(`${apiBase}/gcode-files/chunk/${item.uploadId}/pause`, { method: 'POST' });
                            item.paused = true;
                          }
                          setUploadQueue(q => [...q]);
                        } catch {
                          // Ignore pause/resume errors
                        }
                      }}
                    >{item.paused ? '▶' : 'II'}</button>
                  )}
                  {(item.status === 'uploading' || item.status === 'queued') && (
                    <button
                      className="text-gray-400 hover:text-red-600"
                      title="Cancel upload"
                      onClick={() => {
                        setUploadQueue(q => q.map(it => it.id === item.id ? { ...it, cancelRequested: true } : it));
                        if (item.status === 'uploading') {
                          currentXhrRef.current?.abort();
                        }
                      }}
                    >✕</button>
                  )}
                  {item.finalHash && (
                    <span className="text-gray-400" title={item.finalHash}>{item.finalHash.slice(0,8)}…</span>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      )}

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
                onRename={file.isDirectory ? async (newName: string) => {
                  // Move directory to new name in same parent
                  const parent = file.path === '/' ? '/' : file.path.split('/').slice(0, -1).join('/') || '/';
                  const dest = (parent === '/' ? '' : parent) + '/' + newName;
                  await apiClient.moveGcodePath(file.path, dest, false);
                  queryClient.invalidateQueries({ queryKey: ['gcode-files'] });
                } : undefined}
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
      {showSettings && settings && (
        <div className="mt-6 bg-white border rounded p-4 space-y-4">
          <h3 className="font-semibold text-sm">G-code Upload Settings</h3>
          <div className="flex flex-col gap-2">
            <label className="text-xs font-medium">Allowed Extensions (comma separated)</label>
            <input
              value={extensionsInput}
              onChange={e => setExtensionsInput(e.target.value)}
              className="px-2 py-1 border rounded text-sm"
              placeholder=".gcode, .bgcode"
            />
            <div className="text-xs text-gray-500">Current Limit: {(settings.dailyUploadLimitBytes / (1024*1024)).toFixed(2)} MB/day • Used: {(settings.userUsedBytes / (1024*1024)).toFixed(2)} MB</div>
            <div className="flex gap-2">
              <button
                onClick={async () => {
                  const values = extensionsInput.split(',').map(v => v.trim()).filter(Boolean);
                  if (values.length === 0) { toast.error('Provide at least one extension'); return; }
                  try {
                    await apiClient.updateGcodeUploadSettings(values);
                    const fresh = await apiClient.getGcodeUploadSettings();
                    setSettings(fresh);
                    toast.success('Settings updated');
                  } catch (e:any) {
                    toast.error(e.message || 'Failed');
                  }
                }}
                className="px-3 py-1 bg-blue-600 text-white rounded text-sm"
              >Save</button>
              <button
                onClick={async () => {
                  try { const fresh = await apiClient.getGcodeUploadSettings(); setSettings(fresh); setExtensionsInput(fresh.allowedExtensions.join(', ')); } catch {
                    // Ignore settings reload errors
                  }
                }}
                className="px-3 py-1 border rounded text-sm"
              >Reset</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};