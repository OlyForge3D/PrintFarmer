
import { GcodeHarvestOperation } from '@/types/api';
import { IndexedFilesList } from './IndexedFilesList';

interface HarvestOperationDetailsProps {
  operation: GcodeHarvestOperation;
  onClose?: () => void;
  inline?: boolean; // If true, render as inline panel instead of modal
  className?: string; // Allow custom styling for inline use
  hideCloseButton?: boolean;
}

export function HarvestOperationDetails({ operation, onClose, inline = false, className = '', hideCloseButton = false }: HarvestOperationDetailsProps) {
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
        {operation.error && (
          <tr>
            <th className="text-left font-medium px-4 py-2 text-red-600 dark:text-red-400">Error</th>
            <td className="px-4 py-2 text-red-600 dark:text-red-400">{operation.error}</td>
          </tr>
        )}
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
