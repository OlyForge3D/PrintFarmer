import { useEffect, useState } from 'react';
import { GcodeHarvestOperation, GcodeHarvestStatus } from '@/types/api';
import { IndexedFilesList } from './IndexedFilesList';
import { getHarvestErrorInfo, getPhaseDisplay } from '@/common/utils/harvestErrorHelper';
import { ErrorIcon } from './ErrorIcon';
import { Button } from '@/common/components/ui/Button';
import { apiClient } from '@/services/api';
import { signalRService } from '@/services/harvest-signalr';
import { CloseIcon } from '@/common/components/icons/MdiIcons';

interface HarvestOperationDetailsProps {
  operation?: GcodeHarvestOperation; // Optional - can be provided or fetched
  operationId?: string; // If provided without operation, will fetch from API
  onClose?: () => void;
  inline?: boolean; // If true, render as inline panel instead of modal
  className?: string; // Allow custom styling for inline use
  hideCloseButton?: boolean;
  onFilesImported?: () => void; // Callback when files are successfully imported
}

export function HarvestOperationDetails({ operation: initialOperation, operationId: propOperationId, onClose, inline = false, className = '', hideCloseButton = false, onFilesImported }: HarvestOperationDetailsProps) {
  const [operation, setOperation] = useState<GcodeHarvestOperation | null>(initialOperation || null);
  const [loading, setLoading] = useState(!initialOperation);

  const operationId = operation?.id || propOperationId;

  // Fetch operation from API if only operationId is provided
  // Also re-fetch periodically to ensure UI stays in sync
  useEffect(() => {
    if (!initialOperation && propOperationId) {
      const fetchOperation = () => {
        apiClient.getHarvestOperation(propOperationId)
          .then(op => {
            setOperation(op);
            setLoading(false);
          })
          .catch(err => {
            console.error('Failed to fetch harvest operation:', err);
            setLoading(false);
          });
      };

      setLoading(true);
      fetchOperation();

      // Poll every 5 seconds to keep stats in sync (fallback if SignalR events are missed)
      const interval = setInterval(fetchOperation, 5000);
      return () => clearInterval(interval);
    }
  }, [initialOperation, propOperationId]);

  // Subscribe to operation updates via SignalR
  useEffect(() => {
    if (!operationId) return;

    // Subscribe to operation progress updates
    const unsubProgress = signalRService.onHarvestOperationProgress((progress) => {
      if (progress.operationId === operationId) {
        setOperation(prev => prev ? {
          ...prev,
          filesFound: progress.filesFound,
          filesAdded: progress.filesAdded,
          filesSkipped: progress.filesSkipped,
          filesErrored: progress.filesErrored,
        } : null);
      }
    });

    // Subscribe to operation completion
    const unsubComplete = signalRService.onHarvestOperationCompleted((evt) => {
      if (evt.operationId === operationId) {
        setOperation(prev => prev ? {
          ...prev,
          status: evt.status as GcodeHarvestStatus,
          filesAdded: evt.filesAdded,
          filesSkipped: evt.filesSkipped,
          filesErrored: evt.filesErrored,
          completedAt: evt.completedAt,
        } : null);
      }
    });

    signalRService.connect().then(() => {
      signalRService.joinHarvestGroup(operationId);
    });

    return () => {
      unsubProgress();
      unsubComplete();
    };
  }, [operationId]);

  if (loading || !operation) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-pf-text-secondary">Loading operation details...</div>
      </div>
    );
  }
  // Duration calculation
  const started = new Date(operation.startedAt);
  const completed = operation.completedAt ? new Date(operation.completedAt) : null;
  let duration = '';
  if (completed) {
    const ms = completed.getTime() - started.getTime();
    const min = Math.floor(ms / 60000);
    const sec = Math.floor((ms % 60000) / 1000);
    duration = min > 0 ? `${min}m ${sec}s` : `${sec}s`;
  }

  // Status flags
  const isFailed = operation.status === GcodeHarvestStatus.Failed;
  const isCompleted = operation.status === GcodeHarvestStatus.Completed;
  const isCancelled = operation.status === GcodeHarvestStatus.Cancelled;

  // Don't show cancelled banner if files were discovered (user didn't actually cancel)
  const shouldShowCancelledBanner = isCancelled && operation.filesFound === 0;

  const summaryTable = (
    <div className="mb-3">
      {/* Row 1: Printer name, Status, Started/Completed on same line */}
      <div className="flex items-center gap-6 mb-3">
        <div className="flex-shrink-0">
          <div className="text-lg font-bold text-pf-primary">{operation.printerName}</div>
        </div>
        <div className="flex-shrink-0">
          <span className="text-sm font-medium text-pf-text-1">Status:</span>
          <span className="ml-1 text-sm font-semibold text-pf-text-0">{operation.status}</span>
        </div>
        <div className="flex-1 min-w-0">
          <span className="text-sm font-medium text-pf-text-1">
            {started.toLocaleString()}
            {completed && <span> → {completed.toLocaleString()}</span>}
            {completed && <span className="ml-2 text-xs text-pf-muted">({duration})</span>}
          </span>
        </div>
      </div>
      
      {/* Row 2: File Stats Chicklets */}
      <div className="flex flex-wrap gap-2">
        <div className="h-7 rounded bg-pf-bg-2 border border-pf-border text-pf-text-0 text-xs font-semibold flex items-center justify-center px-2">
          Found <span className="font-bold ml-1">{operation.filesFound}</span>
        </div>
        <div className="h-7 rounded bg-pf-success-bg border border-pf-success-border text-pf-success-text text-xs font-semibold flex items-center justify-center px-2">
          Added <span className="font-bold ml-1">{operation.filesAdded}</span>
        </div>
        <div className="h-7 rounded bg-pf-bg-2 border border-pf-border text-pf-text-secondary text-xs font-semibold flex items-center justify-center px-2">
          Skipped <span className="font-bold ml-1">{operation.filesSkipped}</span>
        </div>
        <div className="h-7 rounded bg-pf-error-bg border border-pf-error-border text-pf-error-text text-xs font-semibold flex items-center justify-center px-2">
          Failed <span className="font-bold ml-1">{operation.filesErrored}</span>
        </div>
        <div className="h-7 rounded bg-pf-info-bg border border-pf-info-border text-pf-info-text text-xs font-semibold flex items-center justify-center px-2">
          Total <span className="font-bold ml-1">{operation.totalSizeBytes}</span>
        </div>
      </div>
    </div>
  );

  const content = (
    <div
      className={`bg-pf-bg-1 rounded-lg w-full h-full flex flex-col p-6 relative ${inline ? 'border border-pf-border' : 'shadow-lg'} ${className}`}
    >
      {!hideCloseButton && onClose && (
        <Button
          variant="subtle"
          size="sm"
          onClick={onClose}
          className="absolute top-2 right-2 !p-1 !h-auto"
          aria-label="Close details"
        >
          <CloseIcon className="h-4 w-4" />
        </Button>
      )}
      <h2 className="text-xl font-bold mb-3 text-pf-text-0 flex-shrink-0">Harvest Operation Details</h2>
      <div className="mb-3 flex-shrink-0">
        {summaryTable}
      </div>

      {/* Enhanced Error Banner */}
      {(isFailed || operation.error) && (() => {
        const errorInfo = getHarvestErrorInfo(operation);
        if (!errorInfo) return null;

        return (
          <div className="bg-pf-error-bg border border-pf-error-border rounded-lg p-3 mb-3 flex-shrink-0">
            <div className="flex items-start gap-3">
              <ErrorIcon type={errorInfo.iconType} />
              <div className="flex-1 min-w-0">
                <p className="font-semibold text-pf-error-text text-sm">{errorInfo.title}</p>
                <p className="text-pf-error-text text-sm mt-1 break-words opacity-90">{errorInfo.message}</p>
                
                {errorInfo.phase && (
                  <p className="text-pf-error-text text-xs mt-1 italic opacity-75">
                    Failed {getPhaseDisplay(errorInfo.phase)}
                  </p>
                )}
                
                {errorInfo.failedResource && (
                  <p className="text-pf-error-text text-xs mt-1 font-mono break-all opacity-75">
                    Resource: {errorInfo.failedResource}
                  </p>
                )}
                
                {errorInfo.suggestion && (
                  <div className="mt-2 p-2 bg-pf-bg-2 border border-pf-border rounded text-xs text-pf-text-secondary">
                    <p className="font-semibold text-pf-text-primary">💡 Suggestion:</p>
                    <p className="mt-0.5">{errorInfo.suggestion}</p>
                  </div>
                )}
                
                {errorInfo.canRetry && (
                  <p className="text-pf-success-text text-xs mt-2 font-medium">
                    🔄 This operation can be retried
                  </p>
                )}
              </div>
            </div>
          </div>
        );
      })()}

      {/* Cancelled Banner */}
      {shouldShowCancelledBanner && (
        <div className="bg-pf-warning-bg border border-pf-warning-border rounded-lg p-3 mb-3 flex-shrink-0">
          <div className="flex items-start gap-3">
            <svg className="w-5 h-5 text-pf-warning-text flex-shrink-0 mt-0.5" fill="currentColor" viewBox="0 0 20 20">
              <path fillRule="evenodd" d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z" clipRule="evenodd" />
            </svg>
            <div className="flex-1 min-w-0">
              <p className="font-semibold text-pf-warning-text text-sm">Harvest Cancelled</p>
              <p className="text-pf-warning-text text-sm mt-1 opacity-90">
                The harvest operation was cancelled by the user.
              </p>
            </div>
          </div>
        </div>
      )}

      {/* Success Banner */}
      {isCompleted && !operation.error && (
        <div className="bg-pf-success-bg border border-pf-success-border rounded-lg p-3 mb-3 flex-shrink-0">
          <div className="flex items-start gap-3">
            <svg className="w-5 h-5 text-pf-success-text flex-shrink-0 mt-0.5" fill="currentColor" viewBox="0 0 20 20">
              <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
            </svg>
            <div className="flex-1 min-w-0">
              <p className="font-semibold text-pf-success-text text-sm">Harvest Completed Successfully</p>
              <p className="text-pf-success-text text-sm mt-1 opacity-90">
                Successfully processed {operation.filesFound} files: {operation.filesAdded} added, {operation.filesSkipped} skipped, {operation.filesErrored} errors
              </p>
            </div>
          </div>
        </div>
      )}

      {/* Footer Buttons - Only shown in standalone mode (not in wizard) */}
      {!hideCloseButton && onClose && (
        <div className="mt-4 flex-shrink-0 flex justify-end gap-2">
          {!isCompleted && !isFailed && !isCancelled && (
            <Button
              onClick={onClose}
              disabled={false}
              className="bg-pf-success-bg hover:bg-pf-success-hover border-pf-success"
            >
              Finished
            </Button>
          )}
          <Button
            onClick={onClose}
            disabled={false}
            variant="secondary"
          >
            Close
          </Button>
        </div>
      )}

      <div className="mb-2 flex-shrink-0">
        <div className="text-md font-semibold text-pf-primary mb-1">Discovered Files</div>
        <div className="text-xs text-pf-muted mb-2">
          You can retry or skip failed files, or import selected files to the library. This list is available for review even after completion or cancellation.
        </div>
      </div>
      <div className="rounded border border-pf-border bg-pf-surface overflow-hidden flex-1 min-h-0">
        <IndexedFilesList operationId={operation.id} onFilesImported={onFilesImported} />
      </div>
    </div>
  );

  if (inline) {
    return <div className="w-full h-full">{content}</div>;
  }
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-40 p-4">
      <div className="w-full max-w-6xl h-5/6 flex flex-col">
        {content}
      </div>
    </div>
  );
}
