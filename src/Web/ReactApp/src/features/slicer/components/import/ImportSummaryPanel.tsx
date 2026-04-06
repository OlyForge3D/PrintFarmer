import clsx from 'clsx';
import { Button, Card, Badge, Spinner } from '@/common/components/ui';
import { CheckCircleIcon, AlertTriangleIcon } from '@/common/components/icons/MdiIcons';

export interface ImportSummaryPanelProps {
  totalProfiles: number;
  selectedCount: number;
  conflictCount: number;
  resolvedCount: number;
  byType: { process: number; machine: number; filament: number };
  onConfirm: () => void;
  onCancel: () => void;
  isImporting?: boolean;
  className?: string;
}

const typeConfig = {
  process: {
    label: 'Process',
    color: 'bg-blue-500/10 text-blue-500',
  },
  machine: {
    label: 'Machine',
    color: 'bg-purple-500/10 text-purple-500',
  },
  filament: {
    label: 'Filament',
    color: 'bg-green-500/10 text-green-500',
  },
};

export function ImportSummaryPanel({
  totalProfiles,
  selectedCount,
  conflictCount,
  resolvedCount,
  byType,
  onConfirm,
  onCancel,
  isImporting = false,
  className,
}: ImportSummaryPanelProps) {
  const unresolvedConflicts = conflictCount - resolvedCount;
  const canConfirm = selectedCount > 0 && unresolvedConflicts === 0 && !isImporting;
  const importProgress = isImporting ? 50 : 0;

  return (
    <Card className={clsx('border-pf-accent', className)}>
      <Card.Body className="p-6">
        <div className="space-y-6">
          {/* Header */}
          <div>
            <h3 className="text-lg font-semibold text-pf-text-primary mb-1">
              Import Summary
            </h3>
            <p className="text-sm text-pf-text-secondary">
              Review the import details before proceeding
            </p>
          </div>

          {/* Stats Grid */}
          <div className="grid grid-cols-2 gap-4">
            {/* Total */}
            <div className="p-4 rounded-lg bg-pf-bg-1 border border-pf-border">
              <div className="text-2xl font-bold text-pf-text-primary">
                {totalProfiles}
              </div>
              <div className="text-sm text-pf-text-secondary mt-1">
                Total Profiles
              </div>
            </div>

            {/* Selected */}
            <div className="p-4 rounded-lg bg-pf-bg-1 border border-pf-border">
              <div className="text-2xl font-bold text-pf-accent">
                {selectedCount}
              </div>
              <div className="text-sm text-pf-text-secondary mt-1">
                Selected
              </div>
            </div>

            {/* Conflicts */}
            <div
              className={clsx(
                'p-4 rounded-lg border',
                conflictCount > 0
                  ? 'bg-pf-warning/10 border-pf-warning/20'
                  : 'bg-pf-bg-1 border-pf-border'
              )}
            >
              <div
                className={clsx(
                  'text-2xl font-bold',
                  conflictCount > 0 ? 'text-pf-warning' : 'text-pf-text-primary'
                )}
              >
                {conflictCount}
              </div>
              <div className="text-sm text-pf-text-secondary mt-1">
                Conflicts
              </div>
            </div>

            {/* Resolved */}
            <div
              className={clsx(
                'p-4 rounded-lg border',
                resolvedCount === conflictCount && conflictCount > 0
                  ? 'bg-pf-success/10 border-pf-success/20'
                  : 'bg-pf-bg-1 border-pf-border'
              )}
            >
              <div
                className={clsx(
                  'text-2xl font-bold',
                  resolvedCount === conflictCount && conflictCount > 0
                    ? 'text-pf-success'
                    : 'text-pf-text-primary'
                )}
              >
                {resolvedCount}
              </div>
              <div className="text-sm text-pf-text-secondary mt-1">
                Resolved
              </div>
            </div>
          </div>

          {/* Breakdown by type */}
          <div>
            <div className="text-sm font-medium text-pf-text-primary mb-3">
              Breakdown by Type
            </div>
            <div className="flex flex-wrap gap-2">
              {Object.entries(byType).map(([type, count]) => (
                <Badge
                  key={type}
                  variant="default"
                  className={clsx(
                    'text-sm py-1.5 px-3',
                    typeConfig[type as keyof typeof typeConfig].color
                  )}
                >
                  <span className="font-medium">{count}</span>{' '}
                  {typeConfig[type as keyof typeof typeConfig].label}
                </Badge>
              ))}
            </div>
          </div>

          {/* Status messages */}
          {unresolvedConflicts > 0 && !isImporting && (
            <div className="flex items-start gap-2 p-3 rounded-lg bg-pf-warning/10 border border-pf-warning/20">
              <AlertTriangleIcon className="w-5 h-5 text-pf-warning flex-shrink-0 mt-0.5" />
              <div className="text-sm text-pf-text-primary">
                <strong className="font-medium">
                  {unresolvedConflicts} unresolved conflict{unresolvedConflicts !== 1 ? 's' : ''}
                </strong>
                <p className="text-pf-text-secondary mt-1">
                  Please resolve all conflicts before importing
                </p>
              </div>
            </div>
          )}

          {canConfirm && !isImporting && (
            <div className="flex items-start gap-2 p-3 rounded-lg bg-pf-success/10 border border-pf-success/20">
              <CheckCircleIcon className="w-5 h-5 text-pf-success flex-shrink-0 mt-0.5" />
              <div className="text-sm text-pf-text-primary">
                <strong className="font-medium">Ready to import</strong>
                <p className="text-pf-text-secondary mt-1">
                  All conflicts resolved. Click "Confirm Import" to proceed.
                </p>
              </div>
            </div>
          )}

          {/* Progress bar */}
          {isImporting && (
            <div>
              <div className="flex items-center justify-between mb-2">
                <span className="text-sm font-medium text-pf-text-primary">
                  Importing profiles...
                </span>
                <span className="text-sm text-pf-text-secondary">
                  {importProgress}%
                </span>
              </div>
              <div className="h-2 bg-pf-bg-1 rounded-full overflow-hidden">
                <div
                  className="h-full bg-pf-accent transition-all duration-300"
                  style={{ width: `${importProgress}%` }}
                />
              </div>
            </div>
          )}

          {/* Actions */}
          <div className="flex items-center gap-3 pt-4 border-t border-pf-border">
            <Button
              variant="primary"
              onClick={onConfirm}
              disabled={!canConfirm}
              loading={isImporting}
              className="flex-1"
            >
              {isImporting ? (
                <>
                  <Spinner size="sm" className="mr-2" />
                  Importing...
                </>
              ) : (
                'Confirm Import'
              )}
            </Button>
            <Button
              variant="secondary"
              onClick={onCancel}
              disabled={isImporting}
              className="flex-1"
            >
              Cancel
            </Button>
          </div>
        </div>
      </Card.Body>
    </Card>
  );
}
