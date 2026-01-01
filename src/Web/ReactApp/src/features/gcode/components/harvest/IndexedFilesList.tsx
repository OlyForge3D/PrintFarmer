import React, { useEffect, useState, useRef } from 'react';
import { apiClient } from '@/services/api';
import { DiscoveredGcodeFileDto, HarvestFileStatus } from '@/types/api';
import type { HarvestFileDiscoveredEvent, HarvestFileProgress, HarvestFileUpdatedEvent } from '@/services/harvest-signalr';
import { toast } from 'sonner';
import { signalRService as harvestSignalRService } from '@/services/harvest-signalr';
import { Button } from '@/common/components/ui/Button';
import { formatPrintTimeMinutes } from '@/common/utils/datetime';

interface FileWithProgress extends DiscoveredGcodeFileDto {
  progress?: {
    bytesCopied: number;
    totalBytes: number;
    percent: number;
  };
  completedAt?: string;
}

interface IndexedFilesListProps {
  operationId: string;
}

export const IndexedFilesList: React.FC<IndexedFilesListProps> = ({ operationId }) => {
  const [files, setFiles] = useState<FileWithProgress[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [isImporting, setIsImporting] = useState(false);
  const filesRef = useRef<FileWithProgress[]>([]);

  // Helper to get status display string
  const getStatusString = (status: HarvestFileStatus | undefined): string => {
    if (status === undefined || status === null) return 'Unknown';
    switch (status) {
      case HarvestFileStatus.Pending: return 'Pending';
      case HarvestFileStatus.InProgress: return 'In Progress';
      case HarvestFileStatus.Complete: return 'Complete';
      case HarvestFileStatus.Failed: return 'Failed';
      case HarvestFileStatus.Cancelled: return 'Cancelled';
      case HarvestFileStatus.Skipped: return 'Skipped';
      default: return `Unknown (${status})`;
    }
  };

  // Import selected files logic
  const handleImportSelected = async () => {
    if (selected.size === 0) return;
    setIsImporting(true);
    try {
      const fileIds = Array.from(selected);
      const result = await apiClient.importSelectedGcodeFiles({
        harvestOperationId: operationId,
        fileIds,
      }, { timeout: 300000 }); // 5-minute timeout for import operations
      
      // Check if the operation was successful or had failures
      const importedCount = result.importedFiles ?? 0;
      const skippedCount = (result.skippedFileIds?.length ?? 0);
      const failedCount = (result.failedFileIds?.length ?? 0);
      
      // Update file statuses from the result - preserve all existing file data including extracted metadata
      setFiles(prev => prev.map(f => {
        const imported = Array.isArray(result.importedFileIds) && result.importedFileIds.includes(f.id);
        const skipped = Array.isArray(result.skippedFileIds) && result.skippedFileIds.includes(f.id);
        const failed = Array.isArray(result.failedFileIds) && result.failedFileIds.includes(f.id);
        if (imported) {
          // Keep ALL existing file data including extracted metadata (slicer, material, nozzle, etc.)
          return { 
            ...f, 
            status: HarvestFileStatus.Complete, 
            error: '', 
            progress: undefined 
          };
        }
        if (skipped) return { 
          ...f, 
          status: HarvestFileStatus.Skipped, 
          error: '', 
          progress: undefined 
        };
        if (failed) return { 
          ...f, 
          status: HarvestFileStatus.Failed, 
          error: result.errorDetails?.[f.id] || f.error, 
          progress: undefined 
        };
        return f;
      }));
      
      filesRef.current = files;
      
      setSelected(new Set());
      
      // Show appropriate toast based on results
      if (!result.success || failedCount > 0) {
        // Show error toast with details about what happened
        const failedFiles = filesRef.current.filter(f => 
          Array.isArray(result.failedFileIds) && result.failedFileIds.includes(f.id)
        );
        const failedFileNames = failedFiles.map(f => f.fileName).join(', ');
        
        // Build comprehensive error message
        const summaryMsg = `Import completed with issues. Imported: ${importedCount}, Skipped: ${skippedCount}, Failed: ${failedCount}`;
        const detailMsg = failedCount > 0 ? `Failed files: ${failedFileNames}` : '';
        toast.error(`${summaryMsg}. ${detailMsg}`, { duration: 8000 });
        
        // Also show specific error details for each failed file
        failedFiles.slice(0, 5).forEach(file => {
          const errorDetail = result.errorDetails?.[file.id];
          if (errorDetail) {
            toast.error(`❌ ${file.fileName}: ${errorDetail}`, { duration: 6000 });
          }
        });
        
        // If there's a general operation error, show that too
        const operationError = result.errorDetails?.['_operation'];
        if (operationError) {
          toast.error(`⚠️ Operation Error: ${operationError}`, { duration: 6000 });
        }
      } else if (importedCount > 0 || skippedCount > 0) {
        toast.success(`Import completed successfully. Imported: ${importedCount}, Skipped: ${skippedCount}`);
      }
    } catch (e: unknown) {
      const msg = e && typeof e === 'object' && 'message' in e ? (e as { message?: string }).message : 'Unknown error';
      toast.error('Import operation failed: ' + (msg || 'Unknown error'), { duration: 6000 });
    } finally {
      setIsImporting(false);
    }
  };

  // Skip a file (call backend and update UI)
  const handleSkipFile = async (fileId: string) => {
    try {
      const ok = await apiClient.skipHarvestFile(operationId, fileId);
      if (ok) {
        setFiles(prev => prev.map(f => f.id === fileId ? { ...f, status: HarvestFileStatus.Skipped, error: '' } : f));
        toast.success('File skipped: ' + fileId);
      } else {
        toast.error('Failed to skip file: ' + fileId);
      }
    } catch (e: unknown) {
      const msg = e && typeof e === 'object' && 'message' in e ? (e as { message?: string }).message : fileId;
      toast.error('Error skipping file: ' + (msg || fileId));
    }
  };

  // Retry a file (call backend and update UI)
  const handleRetryFile = async (fileId: string) => {
    try {
      const ok = await apiClient.retryHarvestFile(operationId, fileId);
      if (ok) {
        setFiles(prev => prev.map(f => f.id === fileId ? { ...f, status: HarvestFileStatus.InProgress, error: '' } : f));
        toast.success('Retry requested for file: ' + fileId);
      } else {
        toast.error('Failed to retry file: ' + fileId);
      }
    } catch (e: unknown) {
      const msg = e && typeof e === 'object' && 'message' in e ? (e as { message?: string }).message : fileId;
      toast.error('Error retrying file: ' + (msg || fileId));
    }
  };

  // Fetch initial files and set up SignalR real-time updates
  useEffect(() => {
    setLoading(true);

  const unsubDiscovered = harvestSignalRService.onHarvestFileDiscovered((evt: HarvestFileDiscoveredEvent) => {
    if (evt.operationId !== operationId) return;
    setFiles(prev => {
      const idx = prev.findIndex(f => f.id === evt.fileId || f.filePath === evt.filePath);
      // Map evt.status string to HarvestFileStatus enum if needed
      let status: HarvestFileStatus | undefined = undefined;
      if (typeof evt.status === 'string') {
        // Map evt.status string to HarvestFileStatus enum
        status = ((HarvestFileStatus as unknown) as Record<string, HarvestFileStatus>)[evt.status] ?? undefined;
      } else if (typeof evt.status === 'number') {
        status = evt.status;
      }
      const updated: Partial<DiscoveredGcodeFileDto> = {
        id: evt.fileId,
        filePath: evt.filePath,
        printerPath: evt.filePath,
        fileName: evt.fileName,
        fileSizeBytes: evt.fileSize,
        modifiedAt: evt.modifiedAt,
        status,
        error: evt.error,
        thumbnailUrl: evt.thumbnailUrl,
        extractedSlicerName: evt.extractedSlicer,
        extractedSlicerVersion: evt.extractedSlicerVersion,
        extractedMaterial: evt.extractedMaterial,
        extractedNozzleDiameter: evt.extractedNozzleDiameter,
        extractedPrintTime: evt.extractedPrintTime,
        extractedFilamentLength: evt.extractedFilamentLength
      };
      if (idx >= 0) {
        const next = [...prev];
        next[idx] = { ...next[idx], ...updated };
        return next;
      } else {
        return [...prev, { ...updated } as DiscoveredGcodeFileDto];
      }
    });
  });

  const unsubProgress = harvestSignalRService.onHarvestFileProgress((progress: HarvestFileProgress) => {
    if (progress.operationId !== operationId) return;
    setFiles(prev => {
      const idx = prev.findIndex(f => f.fileName === progress.fileName);
      if (idx === -1) return prev;
      const next = [...prev];
      const isComplete = progress.percent === 100;
      next[idx] = {
        ...next[idx],
        status: isComplete ? HarvestFileStatus.Complete : HarvestFileStatus.InProgress,
        progress: {
          bytesCopied: progress.bytesCopied,
          totalBytes: progress.totalBytes,
          percent: progress.percent
        }
      };
      
      // Auto-unselect when complete
      if (isComplete) {
        setSelected(prev => {
          const newSet = new Set(prev);
          newSet.delete(next[idx].id);
          return newSet;
        });
      }
      
      return next;
    });
  });

  const unsubFileUpdated = harvestSignalRService.onHarvestFileUpdated((evt: HarvestFileUpdatedEvent) => {
    if (evt.operationId !== operationId) return;
    setFiles(prev => {
      const idx = prev.findIndex(f => f.id === evt.id);
      if (idx === -1) return prev;
      const next = [...prev];
      
      // Parse status string to enum
      let status: HarvestFileStatus | undefined = undefined;
      if (typeof evt.status === 'string') {
        status = ((HarvestFileStatus as unknown) as Record<string, HarvestFileStatus>)[evt.status] ?? undefined;
      } else if (typeof evt.status === 'number') {
        status = evt.status;
      }
      
      // Update file with ALL fields from the event (use ?? to preserve existing only if event field is null/undefined)
      next[idx] = {
        ...next[idx],
        status: status ?? next[idx].status,
        error: evt.error,
        completedAt: evt.completedAt,
        thumbnailUrl: evt.thumbnailUrl ?? next[idx].thumbnailUrl,
        extractedSlicerName: evt.extractedSlicerName ?? next[idx].extractedSlicerName,
        extractedSlicerVersion: evt.extractedSlicerVersion ?? next[idx].extractedSlicerVersion,
        extractedMaterial: evt.extractedMaterial ?? next[idx].extractedMaterial,
        extractedNozzleDiameter: evt.extractedNozzleDiameter ?? next[idx].extractedNozzleDiameter,
        extractedPrintTime: evt.extractedPrintTime ?? next[idx].extractedPrintTime,
        extractedFilamentLength: evt.extractedFilamentLength ?? next[idx].extractedFilamentLength
      };
      
      return next;
    });
  });


  apiClient.getDiscoveredGcodeFiles(operationId)
      .then(res => {
        setFiles(res);
        filesRef.current = res;
        setError(null);
      })
      .catch((e: Error) => setError(e.message || 'Failed to load files'))
      .finally(() => setLoading(false));

    // Join SignalR group for this harvest operation
    harvestSignalRService.connect().then(() => {
      harvestSignalRService.joinHarvestGroup(operationId);
    });


    return () => {
      if (unsubDiscovered) unsubDiscovered();
      if (unsubProgress) unsubProgress();
      if (unsubFileUpdated) unsubFileUpdated();
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
      <div className="flex flex-col items-center justify-center gap-3 py-8 text-pf-primary">
        <svg className="w-8 h-8 text-pf-accent animate-spin" fill="none" viewBox="0 0 24 24"><circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle><path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8z"></path></svg>
        <p className="font-medium">Loading files...</p>
        <p className="text-sm text-pf-text-secondary">Connecting to harvest operation</p>
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
        <svg className="w-5 h-5 text-pf-accent animate-spin" fill="none" viewBox="0 0 24 24"><circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle><path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8z"></path></svg>
        Discovering files... Files will appear here as they are found.
      </div>
    );
  }

  return (
    <div className="flex flex-col h-full">
      <h4 className="font-semibold px-4 pt-3 pb-2 text-pf-primary sticky top-0 bg-pf-surface z-20">Indexed Files</h4>
      <div className="flex-1 overflow-x-auto overflow-y-auto">
        <table className="min-w-full text-sm">
          <thead className="sticky top-0 bg-pf-table-header text-pf-table-header-text z-30">
            <tr>
              <th className="p-2 border-b border-pf-border">
                {/* eslint-disable-next-line local/pf-no-raw-html-controls */}
                <input type="checkbox" checked={selected.size === files.length} onChange={e => setSelected(e.target.checked ? new Set(files.map(f => f.id)) : new Set())} title="Select all files" aria-label="Select all files" />
              </th>
              <th className="p-2 border-b border-pf-border text-left">File</th>
              <th className="p-2 border-b border-pf-border text-left">Progress</th>
              <th className="p-2 border-b border-pf-border text-right">Size</th>
              <th className="p-2 border-b border-pf-border text-left">Slicer</th>
              <th className="p-2 border-b border-pf-border text-left">Material</th>
              <th className="p-2 border-b border-pf-border text-center">Nozzle</th>
              <th className="p-2 border-b border-pf-border text-right">Print Time</th>
              <th className="p-2 border-b border-pf-border text-right">Filament Used</th>
              <th className="p-2 border-b border-pf-border text-center">Status</th>
              <th className="p-2 border-b border-pf-border text-center">Error</th>
              <th className="p-2 border-b border-pf-border text-center">Modified</th>
            </tr>
          </thead>
          <tbody>
            {files.map(file => {
              const status = file.status;
              const error = file.error || '';
              const key = file.id || file.filePath || file.fileName;
              return (
                <tr
                  key={key}
                  className={
                    `${selected.has(file.id) ? 'bg-pf-accent-bg' : 'hover:bg-pf-hover transition'} ${error ? 'border-l-4 border-pf-error' : ''}`
                  }
                  tabIndex={0}
                  aria-label={`File ${file.fileName}, status: ${status}${error ? ', error: ' + error : ''}`}
                >
                  <td className="p-2 border-b border-pf-border text-center">
                    {/* eslint-disable-next-line local/pf-no-raw-html-controls */}
                    <input type="checkbox" checked={selected.has(file.id)} onChange={() => toggleSelect(file.id)} title={`Select file ${file.fileName}`} aria-label={`Select file ${file.fileName}`} />
                  </td>
                  <td className="p-2 border-b border-pf-border font-mono text-pf-primary" title={file.filePath}>
                    <div className="flex items-center gap-2">
                      {file.thumbnailUrl && (
                        <img
                          src={file.thumbnailUrl}
                          alt={file.fileName + ' thumbnail'}
                          className="w-16 h-16 min-w-[64px] min-h-[64px] rounded shadow border border-pf-border bg-pf-surface object-cover"
                          loading="lazy"
                        />
                      )}
                      <span>{file.fileName}</span>
                    </div>
                  </td>
                  <td className="p-2 border-b border-pf-border">
                    {file.progress && (
                      <div className="flex flex-col gap-1">
                        <div className="w-full bg-pf-bg-2 rounded-full h-2 overflow-hidden">
                          <div
                            className="bg-pf-accent h-full transition-all duration-300"
                            style={{ width: `${file.progress.percent}%` }}
                          />
                        </div>
                        <span className="text-xs text-pf-muted">
                          {file.progress.percent}% ({(file.progress.bytesCopied / 1024 / 1024).toFixed(1)}MB / {(file.progress.totalBytes / 1024 / 1024).toFixed(1)}MB)
                        </span>
                      </div>
                    )}
                  </td>
                  <td className="p-2 border-b border-pf-border text-right text-pf-muted">
                    {(file.fileSizeBytes / 1024).toFixed(1)} KB
                  </td>
                  <td className="p-2 border-b border-pf-border text-left text-pf-muted">
                    {file.extractedSlicerName && (
                      <span className="text-xs" title={`Slicer: ${file.extractedSlicerName}${file.extractedSlicerVersion ? ' ' + file.extractedSlicerVersion : ''}`}>
                        {file.extractedSlicerName}{file.extractedSlicerVersion ? ' ' + file.extractedSlicerVersion : ''}
                      </span>
                    )}
                  </td>
                  <td className="p-2 border-b border-pf-border text-left text-pf-muted">
                    {file.extractedMaterial && (
                      <span className="text-xs" title={`Material: ${file.extractedMaterial}`}>{file.extractedMaterial}</span>
                    )}
                  </td>
                  <td className="p-2 border-b border-pf-border text-center text-pf-muted">
                    {file.extractedNozzleDiameter && (
                      <span className="text-xs" title={`Nozzle: ${file.extractedNozzleDiameter}mm`}>{file.extractedNozzleDiameter}mm</span>
                    )}
                  </td>
                  <td className="p-2 border-b border-pf-border text-right text-pf-muted">
                    {file.extractedPrintTime && (
                      <span className="text-xs" title={`Est. print time: ${formatPrintTimeMinutes(file.extractedPrintTime)}`}>{formatPrintTimeMinutes(file.extractedPrintTime)}</span>
                    )}
                  </td>
                  <td className="p-2 border-b border-pf-border text-right text-pf-muted">
                    {file.extractedFilamentLength && (
                      <span className="text-xs" title={`Filament: ${file.extractedFilamentLength}m`}>{file.extractedFilamentLength}m</span>
                    )}
                  </td>
                  <td className="p-2 border-b border-pf-border text-center">
                    {status !== undefined && (
                      <span
                        className={
                          status === HarvestFileStatus.Complete ? 'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-success-bg text-pf-success' :
                          status === HarvestFileStatus.InProgress ? 'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-accent-bg text-pf-accent' :
                          status === HarvestFileStatus.Failed ? 'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-error-bg text-pf-error' :
                          status === HarvestFileStatus.Skipped ? 'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-muted-bg text-pf-muted' :
                          status === HarvestFileStatus.Cancelled ? 'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-muted-bg text-pf-muted' :
                          'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-bg-2 text-pf-muted'
                        }
                        title={getStatusString(status)}
                        aria-label={getStatusString(status)}
                      >
                        {status === HarvestFileStatus.Complete && <span title="Complete" aria-label="Complete">✔️</span>}
                        {status === HarvestFileStatus.InProgress && <span title="In Progress" aria-label="In Progress">⏳</span>}
                        {status === HarvestFileStatus.Failed && <span title="Failed" aria-label="Failed">❌</span>}
                        {status === HarvestFileStatus.Skipped && <span title="Skipped" aria-label="Skipped">⏭️</span>}
                        {status === HarvestFileStatus.Cancelled && <span title="Cancelled" aria-label="Cancelled">🚫</span>}
                        {status === HarvestFileStatus.Pending && <span title="Pending" aria-label="Pending">⏸️</span>}
                        {getStatusString(status)}
                      </span>
                    )}
                  </td>
                  <td className="p-2 border-b border-pf-border text-center">
                    {error && (
                      <span className="inline-block px-2 py-0.5 rounded bg-pf-error-bg text-pf-error mr-2" title={error}>{error}</span>
                    )}
                    {error && (
                      <>
                        {/* eslint-disable-next-line local/pf-no-raw-html-controls */}
                        <button
                          className="inline-flex items-center px-2 py-0.5 rounded bg-pf-muted-bg text-pf-muted hover:bg-pf-accent-bg hover:text-pf-accent focus:outline-none focus:ring-2 focus:ring-pf-accent mr-1"
                          title="Skip this file"
                          aria-label="Skip file"
                          onClick={() => handleSkipFile(file.id)}
                        >
                          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24"><path stroke="currentColor" strokeWidth="2" d="M6 18L18 6M6 6l12 12"/></svg>
                        </button>
                        {/* eslint-disable-next-line local/pf-no-raw-html-controls */}
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
                  <td className="p-2 border-b border-pf-border text-center text-pf-muted">{file.modifiedAt ? new Date(file.modifiedAt).toLocaleString() : ''}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <div className="px-4 py-3 border-t border-pf-border flex items-center justify-between bg-pf-surface">
        <span className="text-pf-muted text-xs">Tip: Use checkboxes to select files to import.</span>
        <Button
          onClick={handleImportSelected}
          disabled={selected.size === 0 || isImporting}
        >
          {isImporting ? (
            <>
              <span className="inline-block w-4 h-4 border-2 border-pf-accent border-t-transparent rounded-full animate-spin mr-2" />
              Importing...
            </>
          ) : (
            <>
              Import Selected <span className="ml-1 font-bold">({selected.size})</span>
            </>
          )}
        </Button>
      </div>
    </div>
  );
};
