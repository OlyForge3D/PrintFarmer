import React from 'react';
import Button from '@/common/components/ui/Button';
import { ProgressBar } from '@/common/components/ui/ProgressBar';
import { PrinterImportProgress } from '@/services/printerHubService';

type ImportProgressItem = PrinterImportProgress;

interface ImportProgressTableProps {
  items: ImportProgressItem[];
  fileName?: string;
  totalCount?: number;
  isComplete?: boolean;
  onCancel?: () => void;
}

const getStatusIcon = (status: string) => {
  switch (status) {
    case 'Imported':
      return <span style={{ color: 'var(--pf-success)' }}>✓</span>;
    case 'Failed':
      return <span style={{ color: 'var(--pf-error)' }}>✗</span>;
    case 'Skipped':
      return <span style={{ color: 'var(--pf-warning)' }}>⊘</span>;
    case 'Pending':
      return <span style={{ color: 'var(--pf-text-secondary)' }}>●</span>;
    default:
      return null;
  }
};

const getStatusClass = (status: string) => {
  switch (status) {
    case 'Imported':
      return 'bg-pf-bg-0/50';
    case 'Failed':
      return 'bg-pf-error/10';
    case 'Skipped':
      return 'bg-pf-warning/10';
    default:
      return '';
  }
};

const ImportProgressTable: React.FC<ImportProgressTableProps> = ({ items, fileName = '', totalCount = 0, isComplete = false, onCancel }) => {
  const successCount = items.filter(i => i.status === 'Imported').length;
  const failedCount = items.filter(i => i.status === 'Failed').length;
  const skippedCount = items.filter(i => i.status === 'Skipped').length;
  const pendingCount = items.filter(i => i.status === 'Pending').length;

  return (
    <div className="flex flex-col gap-4">
      {/* File info */}
      {fileName && (
        <div className="text-sm text-pf-text-secondary">
          <strong>File:</strong> {fileName}
        </div>
      )}

      {/* Progress summary */}
      {totalCount > 0 && (
        <div className="flex gap-4 text-sm flex-wrap">
          <div className="flex items-center gap-1">
            <span className="font-semibold">{successCount}</span>
            <span style={{ color: 'var(--pf-success)' }}>Imported</span>
          </div>
          <div className="flex items-center gap-1">
            <span className="font-semibold">{skippedCount}</span>
            <span style={{ color: 'var(--pf-warning)' }}>Skipped</span>
          </div>
          <div className="flex items-center gap-1">
            <span className="font-semibold">{failedCount}</span>
            <span style={{ color: 'var(--pf-error)' }}>Failed</span>
          </div>
          <div className="flex items-center gap-1">
            <span className="font-semibold">{pendingCount}</span>
            <span style={{ color: 'var(--pf-text-secondary)' }}>Pending</span>
          </div>
        </div>
      )}

      {/* Progress bar */}
      {totalCount > 0 && (
        <ProgressBar
          value={((totalCount - pendingCount) / totalCount) * 100}
          ariaLabel="Import progress"
          showPercent={false}
        />
      )}

      {/* Results table */}
      <div className="max-h-96 overflow-y-auto border border-pf-border rounded-sm">
        <table className="w-full text-sm">
          <thead className="bg-pf-bg-1 sticky top-0 border-b border-pf-border">
            <tr>
              <th className="text-left p-2 font-semibold">#</th>
              <th className="text-left p-2 font-semibold">Name</th>
              <th className="text-left p-2 font-semibold">Status</th>
              <th className="text-left p-2 font-semibold">Details</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item, idx) => (
              <tr
                key={idx}
                className={`border-b border-pf-border ${getStatusClass(item.status)}`}
              >
                <td className="p-2">{item.index + 1}</td>
                <td className="p-2 font-medium">{item.name}</td>
                <td className="p-2">
                  <div className="flex items-center gap-2">
                    {getStatusIcon(item.status)}
                    {item.status}
                  </div>
                </td>
                <td className="p-2 text-xs text-pf-text-secondary">
                  {item.status === 'Imported' 
                    ? (item.id ? `ID: ${item.id}` : 'Successfully imported')
                    : (item.reason || '-')
                  }
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Action buttons */}
      {onCancel && (
        <div className="flex justify-end gap-2">
          <Button
            onClick={onCancel}
            variant={isComplete ? 'primary' : 'secondary'}
          >
            {isComplete ? 'Close' : 'Cancel'}
          </Button>
        </div>
      )}
    </div>
  );
};

export default ImportProgressTable;
