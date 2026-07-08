import React, { useState } from 'react';
import { Link } from 'react-router';
import { formatDistanceToNow } from 'date-fns';
import {
  PlayIcon,
  CheckCircleIcon,
  ExclamationCircleIcon,
  XCircleIcon,
  ChevronDownIcon,
  StopIcon
} from '@heroicons/react/24/outline';

import {
  GcodeHarvestOperation,
  GcodeHarvestStatus
} from '@/types/api';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useCancelHarvestOperation } from '@/common/hooks/useApi';
import { Button } from '@/common/components/ui/Button';
import { ProgressBar } from '@/common/components/ui/ProgressBar';
import { parseApiDateTimeValue, formatDuration } from '@/common/utils/datetime';

interface HarvestOperationCardProps {
  operation: GcodeHarvestOperation;
  showProgress?: boolean;
  onViewDetails?: (operation: GcodeHarvestOperation) => void;
}

// Utility function to format bytes
const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 Bytes';
  const k = 1024;
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

export const HarvestOperationCard: React.FC<HarvestOperationCardProps> = ({
  operation,
  showProgress = false,
  onViewDetails
}) => {
  const { hasPermission } = useAuth();
  const [showDetails, setShowDetails] = useState(false);
  const cancelMutation = useCancelHarvestOperation();

  const statusConfig = {
    [GcodeHarvestStatus.Running]: { color: 'blue', icon: PlayIcon, label: 'Running' },
    [GcodeHarvestStatus.Completed]: { color: 'green', icon: CheckCircleIcon, label: 'Completed' },
    [GcodeHarvestStatus.Failed]: { color: 'red', icon: ExclamationCircleIcon, label: 'Failed' },
    [GcodeHarvestStatus.Cancelled]: { color: 'gray', icon: XCircleIcon, label: 'Cancelled' }
  };

  const config = statusConfig[operation.status];
  const progress = operation.filesProcessed / Math.max(operation.filesFound, 1) * 100;

  const handleCancel = () => {
    if (cancelMutation && typeof cancelMutation.mutate === 'function') {
      // Trigger cancellation for this operation
      // mutate is provided by the hook and accepts the operation id
      cancelMutation.mutate(operation.id);
    }
  };

  return (
    <div className="p-4">
      <div className="flex items-start justify-between">
        <div className="flex items-start space-x-3">
          <div className={`shrink-0 w-8 h-8 rounded-full bg-${config.color}-100 flex items-center justify-center`}>
            <config.icon className={`w-4 h-4 text-${config.color}-600`} />
          </div>

          <div className="flex-1">
            <div className="flex items-center space-x-2">
              <h4 className="font-medium text-pf-text-primary">
                Harvest from {operation.printerName}
              </h4>
              <span className={`inline-flex items-center px-2 py-0.5 rounded-sm text-xs font-medium bg-${config.color}-100 text-${config.color}-800`}>
                {config.label}
              </span>
            </div>

            <div className="mt-1 text-sm text-pf-text-secondary">
              Started {formatDistanceToNow(parseApiDateTimeValue(operation.startedAt), { addSuffix: true })}
              {operation.completedAt && (
                <span> • Duration: {formatDuration(operation.startedAt, operation.completedAt)}</span>
              )}
            </div>

            {/* Progress bar */}
            {showProgress && operation.status === GcodeHarvestStatus.Running && (
              <div className="mt-2 space-y-1">
                <div className="flex items-center justify-between text-xs">
                  <span>{operation.filesProcessed} / {operation.filesFound} files</span>
                  <span>{Math.round(progress)}%</span>
                </div>
                <ProgressBar
                  value={progress}
                  ariaLabel={`${operation.printerName} harvest progress`}
                  showPercent={false}
                  size="xs"
                />
              </div>
            )}

            {/* Results summary */}
            {operation.status === GcodeHarvestStatus.Completed && (
              <div className="mt-2 flex items-center space-x-4 text-sm">
                <span className="text-pf-text-secondary">
                  <span className="font-medium text-pf-success">{operation.filesProcessed}</span> files harvested
                </span>

                {operation.totalSizeBytes && (
                  <span className="text-pf-text-secondary">
                    <span className="font-medium">{formatBytes(operation.totalSizeBytes)}</span> total size
                  </span>
                )}

                {operation.duplicatesSkipped > 0 && (
                  <span className="text-pf-text-secondary">
                    {operation.duplicatesSkipped} duplicates skipped
                  </span>
                )}
              </div>
            )}

            {/* Error message */}
            {operation.status === GcodeHarvestStatus.Failed && operation.error && (
              <div className="mt-2 p-2 bg-pf-error/10 border border-pf-error/30 rounded-sm text-sm text-pf-error">
                {operation.error}
              </div>
            )}
          </div>
        </div>

        <div className="flex items-center space-x-2">
          {operation.status === GcodeHarvestStatus.Running && onViewDetails && (
            <Button onClick={() => onViewDetails(operation)} variant="subtle" className="text-sm text-pf-accent hover:text-pf-accent">View Details</Button>
          )}

          {operation.status === GcodeHarvestStatus.Completed && hasPermission('gcode_harvest', 'read') && (
            <Link
              to={`/files?harvest=${operation.id}`}
              className="text-sm text-pf-accent hover:text-pf-accent"
            >
              View Files
            </Link>
          )}

          {operation.status === GcodeHarvestStatus.Running && hasPermission('gcode_harvest', 'execute') && (
            <Button
              onClick={handleCancel}
              disabled={cancelMutation.isPending}
              variant="danger"
              className="text-sm"
              title="Cancel harvest operation"
              iconLeft={<StopIcon className={`w-4 h-4 ${cancelMutation.isPending ? 'animate-spin' : ''}`} />}
            >
              {cancelMutation.isPending ? 'Cancelling...' : 'Cancel'}
            </Button>
          )}

          <Button onClick={() => setShowDetails(!showDetails)} variant="subtle" className="text-pf-text-tertiary hover:text-pf-text-secondary" title="Toggle details">
            <ChevronDownIcon className={`w-4 h-4 transition-transform ${showDetails ? 'rotate-180' : ''}`} />
          </Button>
        </div>
      </div>

      {/* Expanded details */}
      {showDetails && (
        <div className="mt-4 pt-4 border-t border-pf-border">
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm">
            <div>
              <span className="text-pf-text-secondary">Operation ID:</span>
              <div className="font-mono text-xs text-pf-text-primary mt-1">
                {operation.id.substring(0, 8)}...
              </div>
            </div>

            <div>
              <span className="text-pf-text-secondary">File Types:</span>
              <div className="font-medium text-pf-text-primary mt-1">
                {operation.options?.fileTypes?.join(', ') || 'All'}
              </div>
            </div>

            <div>
              <span className="text-pf-text-secondary">Include Subfolders:</span>
              <div className="font-medium text-pf-text-primary mt-1">
                {operation.options?.includeSubfolders ? 'Yes' : 'No'}
              </div>
            </div>

            <div>
              <span className="text-pf-text-secondary">Min File Size:</span>
              <div className="font-medium text-pf-text-primary mt-1">
                {operation.options?.minFileSize ? formatBytes(operation.options.minFileSize) : 'None'}
              </div>
            </div>
          </div>

          {operation.filesPaths && operation.filesPaths.length > 0 && (
            <div className="mt-4">
              <span className="text-sm text-pf-text-secondary">Sample Files:</span>
              <div className="mt-1 text-xs font-mono space-y-1">
                {operation.filesPaths.slice(0, 3).map((path, i) => (
                  <div key={i} className="text-pf-text-primary truncate">
                    {path}
                  </div>
                ))}
                {operation.filesPaths.length > 3 && (
                  <div className="text-pf-text-secondary">
                    +{operation.filesPaths.length - 3} more files...
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
};