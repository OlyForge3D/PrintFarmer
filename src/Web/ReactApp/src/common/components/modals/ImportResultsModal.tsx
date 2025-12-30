import React from 'react';
import { Button } from '@/common/components/ui';
import { CloseIcon, CheckCircleIcon, AlertIcon, InfoIcon } from '@/common/components/icons/MdiIcons';

interface ImportResult {
  index: number;
  name: string;
  status: 'Success' | 'Skipped' | 'Failed' | 'Pending';
  reason?: string;
}

interface ImportResultsModalProps {
  isOpen: boolean;
  results: ImportResult[] | null;
  importedCount: number;
  skippedCount: number;
  failureCount: number;
  onClose: () => void;
}

export function ImportResultsModal({
  isOpen,
  results,
  importedCount,
  skippedCount,
  failureCount,
  onClose
}: ImportResultsModalProps) {
  if (!isOpen || !results) return null;

  const failedResults = results.filter(r => r.status === 'Failed');
  const skippedResults = results.filter(r => r.status === 'Skipped');
  const successResults = results.filter(r => r.status === 'Success');

  const handleCopyErrors = () => {
    const errorText = failedResults
      .map(r => `${r.name}: ${r.reason || 'Unknown error'}`)
      .join('\n');
    navigator.clipboard.writeText(errorText).then(() => {
      // Visual feedback would be nice but keeping it simple
    });
  };

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
      <div className="bg-pf-panel border border-pf-border rounded-xl shadow-xl max-w-2xl w-full max-h-[80vh] flex flex-col">
        {/* Header */}
        <div className="flex items-center justify-between p-6 border-b border-pf-border flex-shrink-0">
          <h3 className="text-lg font-bold text-pf-text-primary">
            Import Results
          </h3>
          <Button
            variant="subtle"
            size="sm"
            onClick={onClose}
            className="p-1"
          >
            <CloseIcon className="w-5 h-5" />
          </Button>
        </div>

        {/* Content - scrollable */}
        <div className="overflow-y-auto flex-1 p-6">
          {/* Summary Stats */}
          <div className="grid grid-cols-3 gap-4 mb-6">
            <div className="bg-pf-success-bg border border-pf-success-border rounded-lg p-4">
              <div className="text-2xl font-bold text-pf-success-text">{importedCount}</div>
              <div className="text-sm text-pf-text-secondary">Imported</div>
            </div>
            <div className="bg-pf-warning-bg border border-pf-warning-border rounded-lg p-4">
              <div className="text-2xl font-bold text-pf-warning-text">{skippedCount}</div>
              <div className="text-sm text-pf-text-secondary">Skipped</div>
            </div>
            <div className={`${failureCount > 0 ? 'bg-pf-error-bg border border-pf-error-border' : 'bg-pf-success-bg border border-pf-success-border'} rounded-lg p-4`}>
              <div className={`text-2xl font-bold ${failureCount > 0 ? 'text-pf-error-text' : 'text-pf-success-text'}`}>
                {failureCount}
              </div>
              <div className="text-sm text-pf-text-secondary">Failed</div>
            </div>
          </div>

          {/* Detailed Results */}
          {failedResults.length > 0 && (
            <div className="mb-6">
              <h4 className="flex items-center text-pf-error-text font-semibold mb-3">
                <AlertIcon className="w-5 h-5 mr-2" />
                Failed ({failureCount})
              </h4>
              <div className="bg-pf-error-bg border border-pf-error-border rounded-lg p-4 space-y-2">
                {failedResults.map((result, idx) => (
                  <div key={idx} className="text-sm text-pf-error-text">
                    <span className="font-semibold">{result.name}:</span> {result.reason || 'Unknown error'}
                  </div>
                ))}
              </div>
            </div>
          )}

          {skippedResults.length > 0 && (
            <div className="mb-6">
              <h4 className="flex items-center text-pf-warning-text font-semibold mb-3">
                <InfoIcon className="w-5 h-5 mr-2" />
                Skipped ({skippedCount})
              </h4>
              <div className="bg-pf-warning-bg border border-pf-warning-border rounded-lg p-4 space-y-2">
                {skippedResults.map((result, idx) => (
                  <div key={idx} className="text-sm text-pf-warning-text">
                    <span className="font-semibold">{result.name}:</span> {result.reason || 'Duplicate or other reason'}
                  </div>
                ))}
              </div>
            </div>
          )}

          {successResults.length > 0 && (
            <div>
              <h4 className="flex items-center text-pf-success-text font-semibold mb-3">
                <CheckCircleIcon className="w-5 h-5 mr-2" />
                Imported ({importedCount})
              </h4>
              <div className="bg-pf-success-bg border border-pf-success-border rounded-lg p-4 space-y-2">
                {successResults.map((result, idx) => (
                  <div key={idx} className="text-sm text-pf-success-text">
                    {result.name}
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="flex justify-between items-center p-6 border-t border-pf-border flex-shrink-0 bg-pf-panel-dark">
          {failedResults.length > 0 && (
            <Button
              variant="secondary"
              onClick={handleCopyErrors}
              className="mr-3"
            >
              Copy Error Details
            </Button>
          )}
          <Button
            variant="primary"
            onClick={onClose}
            className="ml-auto"
          >
            Close
          </Button>
        </div>
      </div>
    </div>
  );
}
