import React, { useEffect, useState, useRef } from 'react';
import { apiClient } from '@/services/api';
import { GcodeFile } from '@/types/api';
import { toast } from 'sonner';
import { signalRService as harvestSignalRService } from '@/services/signalr';

// Extend GcodeFile for UI status/error
interface GcodeFileWithStatus extends GcodeFile {
  status?: string;
  error?: string;
  percent?: number; // For progress
  bytesCopied?: number;
  totalBytes?: number;
}

interface IndexedFilesListProps {
  operationId: string;
}

export const IndexedFilesList: React.FC<IndexedFilesListProps> = ({ operationId }) => {
  const [files, setFiles] = useState<GcodeFileWithStatus[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  // const [copying, setCopying] = useState(false);
  const filesRef = useRef<GcodeFile[]>([]);

  // Copy selected files logic (stub)
  const handleCopySelected = () => {
    toast.info('Copy selected files: ' + Array.from(selected).join(', '));
  };

  // Retry a file (call backend and update UI)
  const handleRetryFile = async (fileId: string) => {
    try {
      const ok = await apiClient.retryHarvestFile(operationId, fileId);
      if (ok) {
        setFiles(prev => prev.map(f => f.id === fileId ? { ...f, status: 'in-progress', error: '' } : f));
        toast.success('Retry requested for file: ' + fileId);
      } else {
        toast.error('Failed to retry file: ' + fileId);
      }
    } catch (e: any) {
      toast.error('Error retrying file: ' + (e?.message || fileId));
    }
  };

  // Skip a file (call backend and update UI)
  const handleSkipFile = async (fileId: string) => {
    try {
      const ok = await apiClient.skipHarvestFile(operationId, fileId);
      if (ok) {
        setFiles(prev => prev.map(f => f.id === fileId ? { ...f, status: 'skipped', error: '' } : f));
        toast.success('File skipped: ' + fileId);
      } else {
        toast.error('Failed to skip file: ' + fileId);
      }
    } catch (e: any) {
      toast.error('Error skipping file: ' + (e?.message || fileId));
    }
  };

  // Fetch initial files and set up SignalR real-time updates
  useEffect(() => {
    setLoading(true);
    let unsubDiscovered: (() => void) | undefined;
    let unsubProgress: (() => void) | undefined;

    apiClient.getGcodeFilesWithFilter({ harvestId: operationId })
      .then(res => {
        setFiles(res.files);
        filesRef.current = res.files;
        setError(null);
      })
      .catch((e: Error) => setError(e.message || 'Failed to load files'))
      .finally(() => setLoading(false));

    // Join SignalR group for this harvest operation
    harvestSignalRService.connect().then(() => {
      harvestSignalRService.joinHarvestGroup(operationId);
    });

    // Listen for real-time discovered files
    unsubDiscovered = harvestSignalRService.onHarvestFileDiscovered((evt) => {
      if (evt.operationId !== operationId) return;
      setFiles(prev => {
        const idx = prev.findIndex(f => f.id === evt.fileId || f.path === evt.filePath);
        const updated: Partial<GcodeFileWithStatus> = {
          id: evt.fileId,
          path: evt.filePath,
          name: evt.fileName,
          size: evt.fileSize,
          status: evt.status ?? '',
          error: evt.error ?? ''
        };
        if (idx >= 0) {
          const next = [...prev];
          next[idx] = { ...next[idx], ...updated };
          return next;
        } else {
          return [...prev, { ...updated } as GcodeFileWithStatus];
        }
      });
    });

    // Listen for real-time per-file progress updates
    unsubProgress = harvestSignalRService.onHarvestFileProgress((progress) => {
      if (progress.operationId !== operationId) return;
      setFiles(prev => {
        const idx = prev.findIndex(f => f.name === progress.fileName);
        if (idx === -1) return prev;
        const next = [...prev];
        next[idx] = {
          ...next[idx],
          percent: progress.percent,
          bytesCopied: progress.bytesCopied,
          totalBytes: progress.totalBytes,
          status: progress.percent === 100 ? 'completed' : 'in-progress',
        };
        return next;
      });
    });

    return () => {
      if (unsubDiscovered) unsubDiscovered();
      if (unsubProgress) unsubProgress();
      harvestSignalRService.leaveHarvestGroup(operationId);
    };
  }, [operationId]);

  const toggleSelect = (id: string) => {
    setSelected(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };


  if (loading) {
    return (
      <div className="flex items-center gap-2 text-pf-primary animate-pulse">
        <svg className="w-5 h-5 text-pf-accent animate-spin" fill="none" viewBox="0 0 24 24"><circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle><path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8z"></path></svg>
        Loading indexed files...
      </div>
    );
  }
  if (error) {
    return (
      <div className="flex items-center gap-2 text-pf-error bg-pf-error-bg rounded px-3 py-2">
        <svg className="w-5 h-5 text-pf-error" fill="none" viewBox="0 0 24 24"><path stroke="currentColor" strokeWidth="2" d="M12 9v4m0 4h.01M21 12c0 4.97-4.03 9-9 9s-9-4.03-9-9 4.03-9 9-9 9 4.03 9 9Z"/></svg>
        {error}
      </div>
    );
  }
  if (!files.length) {
    return (
      <div className="flex items-center gap-2 text-pf-muted bg-pf-surface rounded px-3 py-2">
        <svg className="w-5 h-5 text-pf-muted" fill="none" viewBox="0 0 24 24"><path stroke="currentColor" strokeWidth="2" d="M12 6v6l4 2"/></svg>
        No indexed files found for this operation.
      </div>
    );
  }

  return (
    <div className="mt-4">
      <h4 className="font-semibold mb-2 text-pf-primary">Indexed Files</h4>
      <div className="overflow-x-auto rounded shadow border border-pf-border bg-pf-surface">
        <table className="min-w-full text-sm">
          <thead className="bg-pf-table-header text-pf-table-header-text">
            <tr>
              <th className="p-2 border-b border-pf-border"><input type="checkbox" checked={selected.size === files.length} onChange={e => setSelected(e.target.checked ? new Set(files.map(f => f.id)) : new Set())} title="Select all files" aria-label="Select all files" /></th>
              <th className="p-2 border-b border-pf-border text-left">Name</th>
              <th className="p-2 border-b border-pf-border text-right">Size</th>
              <th className="p-2 border-b border-pf-border text-center">Status</th>
              <th className="p-2 border-b border-pf-border text-center">Error</th>
              <th className="p-2 border-b border-pf-border text-center">Modified</th>
            </tr>
          </thead>
          <tbody>
            {files.map(file => {
              const status = file.status || '';
              const error = file.error || '';
              // Use file.id if available, otherwise fallback to file.path for key
              const key = file.id || file.path || file.name;
              return (
                <tr
                  key={key}
                  className={
                    `${selected.has(file.id) ? 'bg-pf-accent-bg' : 'hover:bg-pf-hover transition'} ${error ? 'border-l-4 border-pf-error' : ''}`
                  }
                  tabIndex={0}
                  aria-label={`File ${file.name}, status: ${status}${error ? ', error: ' + error : ''}`}
                >
                  <td className="p-2 border-b border-pf-border text-center">
                    <input type="checkbox" checked={selected.has(file.id)} onChange={() => toggleSelect(file.id)} title={`Select file ${file.name}`} aria-label={`Select file ${file.name}`} />
                  </td>
                  <td className="p-2 border-b border-pf-border font-mono text-pf-primary" title={file.path}>{file.name}</td>
                  <td className="p-2 border-b border-pf-border text-right text-pf-muted">
                    {(file.size / 1024).toFixed(1)} KB
                    {typeof file.percent === 'number' && file.status === 'in-progress' && (
                      <span className="ml-2 text-xs text-pf-accent">{file.percent.toFixed(0)}%</span>
                    )}
                  </td>
                  <td className="p-2 border-b border-pf-border text-center">
                    {status && (
                      <span className={
                        status === 'completed' ? 'inline-block px-2 py-0.5 rounded bg-pf-success-bg text-pf-success' :
                        status === 'in-progress' ? 'inline-block px-2 py-0.5 rounded bg-pf-accent-bg text-pf-accent' :
                        status === 'error' ? 'inline-block px-2 py-0.5 rounded bg-pf-error-bg text-pf-error' :
                        'inline-block px-2 py-0.5 rounded bg-pf-muted-bg text-pf-muted'
                      }>
                        {status.charAt(0).toUpperCase() + status.slice(1)}
                      </span>
                    )}
                  </td>
                  <td className="p-2 border-b border-pf-border text-center">
                    {error && (
                      <span className="inline-block px-2 py-0.5 rounded bg-pf-error-bg text-pf-error mr-2" title={error}>{error}</span>
                    )}
                    {error && (
                      <>
                        <button
                          className="inline-flex items-center px-2 py-0.5 rounded bg-pf-muted-bg text-pf-muted hover:bg-pf-accent-bg hover:text-pf-accent focus:outline-none focus:ring-2 focus:ring-pf-accent mr-1"
                          title="Skip this file"
                          aria-label="Skip file"
                          onClick={() => handleSkipFile(file.id)}
                        >
                          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24"><path stroke="currentColor" strokeWidth="2" d="M6 18L18 6M6 6l12 12"/></svg>
                        </button>
                        <button
                          className="inline-flex items-center px-2 py-0.5 rounded bg-pf-accent-bg text-pf-accent hover:bg-pf-accent-dark hover:text-white focus:outline-none focus:ring-2 focus:ring-pf-accent"
                          title="Retry this file"
                          aria-label="Retry file"
                          onClick={() => handleRetryFile(file.id)}
                        >
                          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24"><path stroke="currentColor" strokeWidth="2" d="M12 4v4m0 0a8 8 0 11-8 8"/></svg>
                        </button>
                      </>
                    )}
                  </td>
                  <td className="p-2 border-b border-pf-border text-center text-pf-muted">{new Date(file.modifiedAt).toLocaleString()}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <div className="mt-3 flex items-center gap-3">
        <button
          className={`btn btn-primary bg-pf-accent text-white hover:bg-pf-accent-dark focus:ring-2 focus:ring-pf-accent focus:outline-none px-4 py-2 rounded shadow disabled:opacity-50 disabled:cursor-not-allowed`}
          disabled={selected.size === 0}
          onClick={handleCopySelected}
        >
          <>Copy Selected <span className="ml-1 font-bold">({selected.size})</span></>
        </button>
        <span className="text-pf-muted text-xs">Tip: Use checkboxes to select files to import.</span>
      </div>
    </div>
  );
};
