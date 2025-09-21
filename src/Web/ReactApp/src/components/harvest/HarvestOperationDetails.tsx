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
  const content = (
    <div className={`bg-pf-bg-0 rounded-lg shadow-lg max-w-lg w-full p-6 relative ${className}`}>
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
      <div className="space-y-2 mb-4 pb-4 border-b border-pf-border">
        <div className="text-lg font-semibold text-pf-primary mb-1">Operation Summary</div>
        <div><span className="font-medium">Printer:</span> {operation.printerName}</div>
        <div><span className="font-medium">Status:</span> {operation.status}</div>
        <div><span className="font-medium">Started:</span> {new Date(operation.startedAt).toLocaleString()}</div>
        {operation.completedAt && (
          <div><span className="font-medium">Completed:</span> {new Date(operation.completedAt).toLocaleString()}</div>
        )}
        <div><span className="font-medium">Files Found:</span> {operation.filesFound}</div>
        <div><span className="font-medium">Files Added:</span> {operation.filesAdded}</div>
        <div><span className="font-medium">Files Skipped:</span> {operation.filesSkipped}</div>
        <div><span className="font-medium">Files Errored:</span> {operation.filesErrored}</div>
        <div><span className="font-medium">Total Size:</span> {operation.totalSizeBytes} bytes</div>
        {operation.error && (
          <div className="text-red-600 dark:text-red-400"><span className="font-medium">Error:</span> {operation.error}</div>
        )}
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
    return content;
  }
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-40">
      {content}
    </div>
  );
}
