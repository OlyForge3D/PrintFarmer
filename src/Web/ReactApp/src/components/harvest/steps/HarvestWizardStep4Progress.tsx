import React, { useEffect, useState } from 'react';
// No MdiIcons used in this component
import { CheckCircle, AlertCircle, Loader } from 'lucide-react';
import { signalRService } from '@/services/harvest-signalr';
import { Button } from '@/components/ui/Button';
import { Alert } from '@/components/ui/Alert';

interface HarvestDiscoveredFile {
  id: string;
  name: string;
  size: number;
  path: string;
  slicerName?: string;
  material?: string;
}

interface FileImportStatus {
  fileId: string;
  fileName: string;
  status: 'pending' | 'importing' | 'completed' | 'failed' | 'skipped';
  progress: number;
  error?: string;
}

interface HarvestWizardStep4ProgressProps {
  totalFiles: number;
  selectedFiles: HarvestDiscoveredFile[];
  operationId?: string;
  onCompleted: () => void;
  onCancel?: () => void;
}

export function HarvestWizardStep4Progress({
  totalFiles,
  selectedFiles,
  operationId,
  onCompleted,
  onCancel,
}: HarvestWizardStep4ProgressProps) {
  const [fileStatuses, setFileStatuses] = useState<FileImportStatus[]>([]);
  const [isImporting, setIsImporting] = useState(true);
  const [elapsedSeconds, setElapsedSeconds] = useState(0);
  const [startTime] = useState(Date.now());

  // Initialize file statuses from selected files on component mount
  useEffect(() => {
    if (selectedFiles.length > 0 && fileStatuses.length === 0) {
      const initialStatuses: FileImportStatus[] = selectedFiles.map(file => ({
        fileId: file.id,
        fileName: file.name,
        status: 'pending',
        progress: 0,
      }));
      setFileStatuses(initialStatuses);
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
        console.info(`[Step4] Initialized ${initialStatuses.length} files in pending state`);
      }
    }
  }, [selectedFiles, fileStatuses.length]);

  // Update elapsed time
  useEffect(() => {
    if (!isImporting) return;

    const interval = setInterval(() => {
      setElapsedSeconds(Math.floor((Date.now() - startTime) / 1000));
    }, 1000);

    return () => clearInterval(interval);
  }, [isImporting, startTime]);

  // Subscribe to real SignalR progress events
  useEffect(() => {
    if (!operationId) return;

    const unsubscribe = signalRService.onHarvestFileProgress((evt) => {
      if (evt.operationId === operationId) {
        setFileStatuses(prev => {
          // Find or update file status using fileName as key
          let fileStatus = prev.find(f => f.fileName === evt.fileName);
          
          if (!fileStatus) {
            // New file - create entry (shouldn't happen if initialized properly, but handle it)
            fileStatus = {
              fileId: evt.fileName, // Use fileName as fileId for tracking
              fileName: evt.fileName,
              status: 'importing',
              progress: evt.percent,
            };
            return [...prev, fileStatus];
          }

          // Update existing file from pending to importing
          return prev.map(f =>
            f.fileName === evt.fileName
              ? {
                  ...f,
                  progress: evt.percent,
                  status: evt.percent >= 100 ? 'completed' : 'importing',
                }
              : f
          );
        });

        if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
          console.info(`[Step4] File progress: ${evt.fileName} - ${evt.percent}% (${evt.bytesCopied}/${evt.totalBytes} bytes)`);
        }
      }
    });

    return () => {
      unsubscribe();
    };
  }, [operationId]);

  // Check if all files are complete
  useEffect(() => {
    // Only mark as done if we have received progress events for files
    if (fileStatuses.length === 0 || totalFiles === 0) return;
    
    const completedCount = fileStatuses.filter(f => f.status === 'completed').length;
    const failedCount = fileStatuses.filter(f => f.status === 'failed').length;
    const totalProcessed = completedCount + failedCount;
    
    if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
      console.info(`[Step4] Progress check: ${completedCount} completed, ${failedCount} failed out of ${totalFiles} total (files received: ${fileStatuses.length})`);
    }
    
    // If all files are either completed or failed, we're done importing
    if (totalProcessed === totalFiles) {
      setIsImporting(false);
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
        console.info(`[Step4] Import complete: ${completedCount} completed, ${failedCount} failed`);
      }
    }
  }, [fileStatuses, totalFiles]);

  // Don't initialize with placeholder data - let SignalR events populate the list
  // Previously this was creating phantom files which prevented detecting completion

  const completedCount = fileStatuses.filter(f => f.status === 'completed').length;
  const failedCount = fileStatuses.filter(f => f.status === 'failed').length;
  const skippedCount = fileStatuses.filter(f => f.status === 'skipped').length;
  const progressPercent = totalFiles > 0 ? Math.round((completedCount / totalFiles) * 100) : 0;

  const formatTime = (seconds: number) => {
    const hrs = Math.floor(seconds / 3600);
    const mins = Math.floor((seconds % 3600) / 60);
    const secs = seconds % 60;

    if (hrs > 0) return `${hrs}h ${mins}m ${secs}s`;
    if (mins > 0) return `${mins}m ${secs}s`;
    return `${secs}s`;
  };

  return (
    <div className="space-y-6">
      <div className="bg-pf-surface border border-pf-border rounded-lg p-4">
        <div className="space-y-3">
          {/* Progress bar */}
          <div>
            <div className="flex items-center justify-between mb-2">
              <span className="text-sm font-medium text-pf-text-primary">
                Import Progress
              </span>
              <span className="text-sm text-pf-text-secondary">
                {completedCount}/{totalFiles} files • {progressPercent}%
              </span>
            </div>
            {/* Text-based progress indicator */}
            <div className="bg-pf-border rounded-lg p-2 text-center">
              <div className="text-sm font-mono text-pf-text-secondary">
                {'['}
                {Array.from({ length: 20 }).map((_, i) =>
                  i < Math.round((progressPercent / 100) * 20) ? '█' : '░'
                )}
                {']'}
              </div>
            </div>
          </div>

          {/* Stats */}
          <div className="grid grid-cols-3 gap-3">
            <div className="bg-pf-bg p-2 rounded text-center">
              <div className="text-lg font-semibold text-pf-success">{completedCount}</div>
              <div className="text-xs text-pf-text-secondary">Imported</div>
            </div>
            {failedCount > 0 && (
              <div className="bg-pf-bg p-2 rounded text-center">
                <div className="text-lg font-semibold text-pf-error">{failedCount}</div>
                <div className="text-xs text-pf-text-secondary">Failed</div>
              </div>
            )}
            {skippedCount > 0 && (
              <div className="bg-pf-bg p-2 rounded text-center">
                <div className="text-lg font-semibold text-pf-warning">{skippedCount}</div>
                <div className="text-xs text-pf-text-secondary">Skipped</div>
              </div>
            )}
          </div>

          {/* Time */}
          <div className="text-center">
            <span className="text-sm text-pf-text-secondary">
              Elapsed: <span className="font-mono font-medium">{formatTime(elapsedSeconds)}</span>
            </span>
            {isImporting && (
              <span className="text-sm text-pf-text-secondary ml-4">
                Importing: {fileStatuses.filter(f => f.status === 'importing').length} file
                {fileStatuses.filter(f => f.status === 'importing').length !== 1 ? 's' : ''}
              </span>
            )}
          </div>
        </div>
      </div>

      {/* File list */}
      <div className="space-y-2">
        <h3 className="text-sm font-medium text-pf-text-primary">Import Details</h3>
        <div className="max-h-96 overflow-y-auto space-y-2 border border-pf-border rounded-lg p-3 bg-pf-bg">
          {fileStatuses.map(file => {
            // Find the selected file info to get additional details
            const selectedFile = selectedFiles.find(f => f.name === file.fileName);
            
            return (
              <div
                key={file.fileId}
                className="flex flex-col gap-2 p-3 rounded border border-pf-border bg-pf-surface"
              >
                {/* File name and status */}
                <div className="flex items-center gap-3">
                  <div className="flex-shrink-0 w-5 h-5">
                    {file.status === 'pending' && (
                      <div className="w-5 h-5 border-2 border-pf-border rounded-full" />
                    )}
                    {file.status === 'importing' && (
                      <Loader className="w-5 h-5 text-pf-accent animate-spin" />
                    )}
                    {file.status === 'completed' && (
                      <CheckCircle className="w-5 h-5 text-pf-success" />
                    )}
                    {file.status === 'failed' && (
                      <AlertCircle className="w-5 h-5 text-pf-error" />
                    )}
                    {file.status === 'skipped' && (
                      <div className="w-5 h-5 border-2 border-pf-warning rounded-full flex items-center justify-center">
                        <div className="w-2 h-2 bg-pf-warning rounded-full" />
                      </div>
                    )}
                  </div>
                  <div className="flex-1 min-w-0">
                    <div className="text-sm font-medium text-pf-text-primary truncate">{file.fileName}</div>
                    {selectedFile && (
                      <div className="text-xs text-pf-text-secondary truncate">
                        {(selectedFile.size / 1024 / 1024).toFixed(2)} MB
                      </div>
                    )}
                    {file.error && (
                      <div className="text-xs text-pf-error truncate">{file.error}</div>
                    )}
                  </div>
                  <div className="text-xs font-mono text-pf-text-secondary flex-shrink-0">
                    {file.status === 'importing' && `${Math.round(file.progress)}%`}
                    {file.status === 'completed' && 'Done'}
                    {file.status === 'pending' && 'Waiting'}
                    {file.status === 'failed' && 'Failed'}
                    {file.status === 'skipped' && 'Skipped'}
                  </div>
                </div>

                {/* Progress bar */}
                {(file.status === 'importing' || file.status === 'completed') && (
                  <div className="flex items-center gap-2">
                    <div className="flex-1 h-2 bg-pf-border rounded-full overflow-hidden">
                      <div
                        className={`h-full transition-all ${
                          file.status === 'completed'
                            ? 'bg-pf-success'
                            : 'bg-pf-accent'
                        }`}
                        style={{ width: `${Math.max(0, Math.min(100, file.progress))}%` } as React.CSSProperties}
                      />
                    </div>
                    <div className="text-xs text-pf-text-secondary w-8 text-right">
                      {Math.round(file.progress)}%
                    </div>
                  </div>
                )}

                {/* Action buttons */}
                {(file.status === 'importing' || file.status === 'failed') && (
                  <div className="flex gap-2 pt-1">
                    {file.status === 'failed' && (
                      <>
                        <Button
                          variant="primary"
                          size="sm"
                          onClick={() => {
                            // Retry - mark as pending so it can be reimported
                            setFileStatuses(prev =>
                              prev.map(f =>
                                f.fileId === file.fileId
                                  ? { ...f, status: 'pending', progress: 0, error: undefined }
                                  : f
                              )
                            );
                          }}
                        >
                          Retry
                        </Button>
                      </>
                    )}
                    {file.status === 'importing' && (
                      <Button
                        variant="danger"
                        size="sm"
                        onClick={() => {
                          // Cancel - mark as skipped
                          setFileStatuses(prev =>
                            prev.map(f =>
                              f.fileId === file.fileId
                                ? { ...f, status: 'skipped', error: 'Cancelled by user' }
                                : f
                            )
                          );
                        }}
                      >
                        Cancel
                      </Button>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>

      {/* Actions */}
      <div className="flex gap-3">
        {isImporting && onCancel && (
          <Button
            variant="secondary"
            size="md"
            onClick={onCancel}
            iconLeft={<X className="w-4 h-4" />}
            className="flex-1"
          >
            Cancel Import
          </Button>
        )}
        {!isImporting && (
          <Button
            variant="primary"
            size="md"
            onClick={onCompleted}
            iconLeft={<CheckCircle className="w-4 h-4" />}
            className="flex-1"
          >
            Complete
          </Button>
        )}
      </div>

      {!isImporting && (
        <Alert type="success">
          ✓ Import completed! {completedCount} file{completedCount !== 1 ? 's' : ''} imported successfully.
        </Alert>
      )}
    </div>
  );
}
