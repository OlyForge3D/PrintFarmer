import React, { useEffect, useState, useRef } from 'react';
import { apiClient } from '@/services/api';
import { DiscoveredGcodeFileDto, HarvestFileStatus } from '@/types/api';
import type { HarvestFileDiscoveredEvent, HarvestFileProgress, HarvestFileUpdatedEvent } from '@/services/harvest-signalr';
import { toast } from 'sonner';
import { signalRService as harvestSignalRService } from '@/services/harvest-signalr';
import { Button } from '@/common/components/ui/Button';
import { Modal } from '@/common/components/ui/Modal';
import { formatPrintTimeMinutes } from '@/common/utils/datetime';

const PAGE_SIZE_OPTIONS = [10, 25, 50, 100];

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
  onFilesImported?: () => void;
}

export const IndexedFilesList: React.FC<IndexedFilesListProps> = ({ operationId, onFilesImported }) => {
  const [files, setFiles] = useState<FileWithProgress[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [isImporting, setIsImporting] = useState(false);
  const [errorModalFile, setErrorModalFile] = useState<FileWithProgress | null>(null);
  const [itemsPerPage, setItemsPerPage] = useState(10);
  const [currentPage, setCurrentPage] = useState(0);
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
        // Notify parent component that files have been imported
        onFilesImported?.();
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
        // Merge API results with any files that arrived via SignalR
        // This prevents losing files that arrived while the API call was in flight
        setFiles(prev => {
          // Create a map of existing files by ID for efficient lookup
          const existingMap = new Map(prev.map(f => [f.id, f]));
          
          // Add/update files from API response
          const merged = new Map(existingMap);
          res.forEach(apiFile => {
            if (merged.has(apiFile.id)) {
              // Merge with existing file, preserving any SignalR-provided data
              const existing = merged.get(apiFile.id)!;
              merged.set(apiFile.id, {
                ...existing,
                ...apiFile,
                // Keep status if already set via SignalR
                status: existing.status ?? apiFile.status,
              });
            } else {
              // Add new file from API
              merged.set(apiFile.id, apiFile);
            }
          });
          
          const result = Array.from(merged.values());
          filesRef.current = result;
          return result;
        });
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

  const handlePageSizeChange = (newSize: number) => {
    setItemsPerPage(newSize);
    setCurrentPage(0); // Reset to first page when size changes
  };
  const totalPages = Math.ceil(files.length / itemsPerPage);
  const startIdx = currentPage * itemsPerPage;
  const endIdx = startIdx + itemsPerPage;
  const paginatedFiles = files.slice(startIdx, endIdx);

  return (
    <div className="flex flex-col h-full">
      <h4 className="font-semibold px-4 pt-3 pb-2 text-pf-primary sticky top-0 bg-pf-surface z-20">Indexed Files</h4>
      <div className="flex-1 overflow-x-auto overflow-y-auto">
        <table className="min-w-full text-sm">
          <thead className="sticky top-0 bg-pf-table-header text-pf-table-header-text z-30">
            <tr>
              <th className="p-2 border-b border-pf-border whitespace-nowrap">
                {/* eslint-disable-next-line local/pf-no-raw-html-controls */}
                <input type="checkbox" checked={paginatedFiles.length > 0 && paginatedFiles.every(f => selected.has(f.id))} onChange={e => setSelected(e.target.checked ? new Set([...selected, ...paginatedFiles.map(f => f.id)]) : new Set([...selected].filter(id => !paginatedFiles.map(f => f.id).includes(id))))} title="Select all files on this page" aria-label="Select all files on this page" />
              </th>
              <th className="p-2 border-b border-pf-border text-left whitespace-nowrap">File</th>
              {files.some(f => f.progress) && (
                <th className="p-2 border-b border-pf-border text-left whitespace-nowrap">Progress</th>
              )}
              <th className="p-2 border-b border-pf-border text-right whitespace-nowrap">Size</th>
              <th className="p-2 border-b border-pf-border text-center whitespace-nowrap">Status</th>
            </tr>
          </thead>
          <tbody>
            {paginatedFiles.map(file => {
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
                      <span className="text-xs">{file.fileName}</span>
                    </div>
                  </td>
                  {files.some(f => f.progress) && (
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
                  )}
                  <td className="p-2 border-b border-pf-border text-right text-pf-muted">
                    <span className="text-xs">{(file.fileSizeBytes / 1024).toFixed(1)} KB</span>
                  </td>
                  <td className="p-2 border-b border-pf-border text-center">
                    {status !== undefined && (
                      <button
                        onClick={() => status === HarvestFileStatus.Failed && error && setErrorModalFile(file)}
                        disabled={status !== HarvestFileStatus.Failed || !error}
                        className={
                          status === HarvestFileStatus.Complete ? 'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-success-bg text-pf-success' :
                          status === HarvestFileStatus.InProgress ? 'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-accent-bg text-pf-accent' :
                          status === HarvestFileStatus.Failed && error ? 'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-error-bg text-pf-error cursor-pointer hover:opacity-80 transition' :
                          status === HarvestFileStatus.Failed ? 'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-error-bg text-pf-error' :
                          status === HarvestFileStatus.Skipped ? 'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-muted-bg text-pf-muted' :
                          status === HarvestFileStatus.Cancelled ? 'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-muted-bg text-pf-muted' :
                          'inline-flex items-center gap-1 px-2 py-0.5 rounded bg-pf-bg-2 text-pf-muted'
                        }
                        title={status === HarvestFileStatus.Failed && error ? 'Click to view error details' : getStatusString(status)}
                        aria-label={getStatusString(status)}
                      >
                        {status === HarvestFileStatus.Complete && <span title="Complete" aria-label="Complete">✔️</span>}
                        {status === HarvestFileStatus.InProgress && <span title="In Progress" aria-label="In Progress">⏳</span>}
                        {status === HarvestFileStatus.Failed && <span title="Failed" aria-label="Failed">❌</span>}
                        {status === HarvestFileStatus.Skipped && <span title="Skipped" aria-label="Skipped">⏭️</span>}
                        {status === HarvestFileStatus.Cancelled && <span title="Cancelled" aria-label="Cancelled">🚫</span>}
                        {status === HarvestFileStatus.Pending && <span title="Pending" aria-label="Pending">⏸️</span>}
                        {getStatusString(status)}
                      </button>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <div className="px-4 py-3 border-t border-pf-border flex items-center justify-between gap-4 bg-pf-surface">
        <div className="flex items-center gap-3">
          <div className="flex items-center gap-2">
            <label htmlFor="pageSize" className="text-sm text-pf-text-secondary">
              Items per page:
            </label>
            <select
              id="pageSize"
              value={itemsPerPage}
              onChange={(e) => handlePageSizeChange(Number(e.target.value))}
              className="px-2 py-1 text-sm border border-pf-border rounded bg-pf-bg-secondary text-pf-text-primary focus:outline-none focus:ring-1 focus:ring-pf-accent min-w-[75px]"
            >
              {PAGE_SIZE_OPTIONS.map(size => (
                <option key={size} value={size}>{size}</option>
              ))}
            </select>
          </div>
          <span className="text-pf-muted text-xs">
            Showing {files.length === 0 ? 0 : startIdx + 1}-{Math.min(endIdx, files.length)} of {files.length} files
          </span>
          {totalPages > 1 && (
            <div className="flex gap-1 ml-2">
              <Button
                variant="secondary"
                size="sm"
                disabled={currentPage === 0}
                onClick={() => setCurrentPage(0)}
                className="!px-2 !py-1"
                title="First page"
              >
                «
              </Button>
              <Button
                variant="secondary"
                size="sm"
                disabled={currentPage === 0}
                onClick={() => setCurrentPage(currentPage - 1)}
                className="!px-2 !py-1"
                title="Previous page"
              >
                ‹
              </Button>
              <span className="text-pf-muted text-xs px-2 py-1">Page {currentPage + 1} of {totalPages}</span>
              <Button
                variant="secondary"
                size="sm"
                disabled={currentPage === totalPages - 1}
                onClick={() => setCurrentPage(currentPage + 1)}
                className="!px-2 !py-1"
                title="Next page"
              >
                ›
              </Button>
              <Button
                variant="secondary"
                size="sm"
                disabled={currentPage === totalPages - 1}
                onClick={() => setCurrentPage(totalPages - 1)}
                className="!px-2 !py-1"
                title="Last page"
              >
                »
              </Button>
            </div>
          )}
        </div>
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

      {/* Error Details Modal */}
      {errorModalFile && (
        <Modal isOpen={!!errorModalFile} onClose={() => setErrorModalFile(null)} title="File Import Error" size="md">
          <div className="space-y-4">
            <div>
              <h3 className="font-semibold text-pf-primary mb-2">File Name</h3>
              <p className="text-pf-text-secondary font-mono text-sm">{errorModalFile.fileName}</p>
            </div>
            <div>
              <h3 className="font-semibold text-pf-primary mb-2">Error Details</h3>
              <p className="text-pf-error bg-pf-error-bg rounded px-3 py-2 text-sm">{errorModalFile.error || 'No error details available'}</p>
            </div>
            <div className="flex gap-2 justify-end pt-4">
              <Button
                variant="secondary"
                onClick={() => handleSkipFile(errorModalFile.id)}
                title="Skip this file"
              >
                Skip File
              </Button>
              <Button
                variant="primary"
                onClick={() => {
                  handleRetryFile(errorModalFile.id);
                  setErrorModalFile(null);
                }}
                title="Retry this file"
              >
                Retry Import
              </Button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
};
