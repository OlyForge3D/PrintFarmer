
import { GcodeHarvestOperation, GcodeHarvestStatus } from '@/types/api';
import { IndexedFilesList } from './IndexedFilesList';
import { getHarvestErrorInfo, getPhaseDisplay } from '@/utils/harvestErrorHelper';
import { ErrorIcon } from './ErrorIcon';

interface HarvestOperationDetailsProps {
  operation: GcodeHarvestOperation;
  onClose?: () => void;
  inline?: boolean; // If true, render as inline panel instead of modal
  className?: string; // Allow custom styling for inline use
  hideCloseButton?: boolean;
  perFileProgress?: Record<string, import('@/services/harvest-signalr').HarvestFileProgress>;
}

export function HarvestOperationDetails({ operation, onClose, inline = false, className = '', hideCloseButton = false, perFileProgress = {} }: HarvestOperationDetailsProps) {
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

  const summaryTable = (
    <table className="w-full text-sm mb-4 border border-pf-border rounded bg-pf-bg-0">
      <tbody>
        {/* Printer name only, no label */}
        <tr>
          <td className="px-4 py-2 font-bold text-lg text-pf-primary" colSpan={2}>{operation.printerName}</td>
        </tr>
        {/* Status row */}
        <tr>
          <th className="text-left font-medium px-4 py-2 w-48">Status</th>
          <td className="px-4 py-2">{operation.status}</td>
        </tr>
        {/* Started, Completed, Duration on one line */}
        <tr>
          <th className="text-left font-medium px-4 py-2">Started / Completed / Duration</th>
          <td className="px-4 py-2">
            <span>{started.toLocaleString()}</span>
            {completed && <span> &rarr; {completed.toLocaleString()}</span>}
            {completed && <span className="ml-2 text-xs text-pf-muted">({duration})</span>}
          </td>
        </tr>
        {/* Chicklets row */}
        <tr>
          <th className="text-left font-medium px-4 py-2 align-top">File Stats</th>
          <td className="px-4 py-2">
            <div className="flex flex-wrap gap-2">
              <div className="w-20 h-8 rounded bg-pf-bg-2 border border-pf-border text-pf-text-0 text-xs font-semibold flex items-center justify-center">
                Found&nbsp;<span className="font-bold">{operation.filesFound}</span>
              </div>
              <div className="w-20 h-8 rounded bg-green-100 border border-green-300 text-green-900 text-xs font-semibold flex items-center justify-center">
                Added&nbsp;<span className="font-bold">{operation.filesAdded}</span>
              </div>
              <div className="w-20 h-8 rounded bg-gray-100 border border-gray-300 text-gray-800 text-xs font-semibold flex items-center justify-center">
                Skipped&nbsp;<span className="font-bold">{operation.filesSkipped}</span>
              </div>
              <div className="w-20 h-8 rounded bg-red-100 border border-red-300 text-red-900 text-xs font-semibold flex items-center justify-center">
                Errored&nbsp;<span className="font-bold">{operation.filesErrored}</span>
              </div>
              <div className="w-28 h-8 rounded bg-blue-100 border border-blue-300 text-blue-900 text-xs font-semibold flex items-center justify-center">
                Total&nbsp;<span className="font-bold">{operation.totalSizeBytes} bytes</span>
              </div>
            </div>
          </td>
        </tr>
      </tbody>
    </table>
  );

  const content = (
    <div
      className={`bg-pf-bg-0 rounded-lg w-full p-6 relative ${inline ? 'border border-pf-border' : 'shadow-lg'} ${className}`}
    >
      {!hideCloseButton && onClose && (
        <button
          className="absolute top-2 right-2 text-pf-text-1 hover:text-pf-accent"
          onClick={onClose}
          aria-label="Close details"
        >
          ×
        </button>
      )}
      <h2 className="text-xl font-bold mb-4 text-pf-text-0">Harvest Operation Details</h2>
      <div className="mb-4">
        {summaryTable}
      </div>

      {/* Enhanced Error Banner */}
      {(isFailed || operation.error) && (() => {
        const errorInfo = getHarvestErrorInfo(operation);
        if (!errorInfo) return null;

        return (
          <div className="bg-red-50 border border-red-300 rounded-lg p-3 mb-4">
            <div className="flex items-start gap-3">
              <ErrorIcon type={errorInfo.iconType} />
              <div className="flex-1 min-w-0">
                <p className="font-semibold text-red-800 text-sm">{errorInfo.title}</p>
                <p className="text-red-700 text-sm mt-1 break-words">{errorInfo.message}</p>
                
                {errorInfo.phase && (
                  <p className="text-red-600 text-xs mt-1 italic">
                    Failed {getPhaseDisplay(errorInfo.phase)}
                  </p>
                )}
                
                {errorInfo.failedResource && (
                  <p className="text-red-600 text-xs mt-1 font-mono break-all">
                    Resource: {errorInfo.failedResource}
                  </p>
                )}
                
                {errorInfo.suggestion && (
                  <div className="mt-2 p-2 bg-red-100 border border-red-200 rounded text-xs text-red-900">
                    <p className="font-semibold">💡 Suggestion:</p>
                    <p className="mt-0.5">{errorInfo.suggestion}</p>
                  </div>
                )}
                
                {errorInfo.canRetry && (
                  <p className="text-green-700 text-xs mt-2 font-medium">
                    🔄 This operation can be retried
                  </p>
                )}
              </div>
            </div>
          </div>
        );
      })()}

      {/* Cancelled Banner */}
      {isCancelled && (
        <div className="bg-yellow-50 border border-yellow-300 rounded-lg p-3 mb-4">
          <div className="flex items-start gap-3">
            <svg className="w-5 h-5 text-yellow-600 flex-shrink-0 mt-0.5" fill="currentColor" viewBox="0 0 20 20">
              <path fillRule="evenodd" d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z" clipRule="evenodd" />
            </svg>
            <div className="flex-1 min-w-0">
              <p className="font-semibold text-yellow-800 text-sm">Harvest Cancelled</p>
              <p className="text-yellow-700 text-sm mt-1">
                The harvest operation was cancelled by the user.
              </p>
            </div>
          </div>
        </div>
      )}

      {/* Success Banner */}
      {isCompleted && !operation.error && (
        <div className="bg-green-50 border border-green-300 rounded-lg p-3 mb-4">
          <div className="flex items-start gap-3">
            <svg className="w-5 h-5 text-green-600 flex-shrink-0 mt-0.5" fill="currentColor" viewBox="0 0 20 20">
              <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
            </svg>
            <div className="flex-1 min-w-0">
              <p className="font-semibold text-green-800 text-sm">Harvest Completed Successfully</p>
              <p className="text-green-700 text-sm mt-1">
                Successfully processed {operation.filesFound} files: {operation.filesAdded} added, {operation.filesSkipped} skipped, {operation.filesErrored} errors
              </p>
            </div>
          </div>
        </div>
      )}

      {Object.keys(perFileProgress).length > 0 && (
        <div className="mb-4">
          <div className="text-md font-semibold text-pf-primary mb-1">Per-File Progress</div>
          <div className="max-h-48 overflow-y-auto border border-pf-border rounded bg-pf-surface">
            <table className="w-full text-xs">
              <thead>
                <tr>
                  <th className="px-2 py-1 text-left">File Name</th>
                  <th className="px-2 py-1 text-left">Progress</th>
                  <th className="px-2 py-1 text-left">Bytes</th>
                </tr>
              </thead>
              <tbody>
                {Object.values(perFileProgress).map(f => (
                  <tr key={f.fileName}>
                    <td className="px-2 py-1">{f.fileName}</td>
                    <td className="px-2 py-1">{f.percent}%</td>
                    <td className="px-2 py-1">{f.bytesCopied} / {f.totalBytes}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <div className="mb-2">
        <div className="text-md font-semibold text-pf-primary mb-1">Discovered Files</div>
        <div className="text-xs text-pf-muted mb-2">
          You can retry or skip errored files, or import selected files to the library. This list is available for review even after completion or cancellation.
        </div>
      </div>
      <div className="max-h-80 overflow-y-auto rounded border border-pf-border bg-pf-surface">
        <IndexedFilesList operationId={operation.id} />
      </div>
    </div>
  );

  if (inline) {
    return <div className="w-full">{content}</div>;
  }
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-40">
      {content}
    </div>
  );
}
