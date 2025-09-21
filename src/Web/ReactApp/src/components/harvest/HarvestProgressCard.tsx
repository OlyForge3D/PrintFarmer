import React from 'react';
import { formatDistanceToNow } from 'date-fns';
import {
  PlayIcon,
  CheckCircleIcon,
  ExclamationCircleIcon,
  XCircleIcon,
  StopIcon
} from '@heroicons/react/24/outline';
import {
  GcodeHarvestOperation,
  GcodeHarvestStatus
} from '@/types/api';
import { useAuth } from '@/contexts/AuthContext';
import { useCancelHarvestOperation } from '@/hooks/useApi';
import { toast } from 'sonner';
import { parseApiDateTimeValue, formatDuration } from '@/utils/datetime';

interface FileProgress {
  fileName: string;
  percent: number;
  status: 'processing' | 'completed' | 'skipped' | 'errored';
}

interface HarvestProgressCardProps {
  operation: GcodeHarvestOperation;
  onOperationUpdate?: () => void;
  perFileProgress?: Record<string, FileProgress>;
  onViewDetails?: (operation: GcodeHarvestOperation) => void;
}

const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 Bytes';
  const k = 1024;
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

export const HarvestProgressCard: React.FC<HarvestProgressCardProps> = ({
  operation,
  onOperationUpdate,
  perFileProgress,
  onViewDetails
}) => {
  const { hasPermission } = useAuth();
  const cancelMutation = useCancelHarvestOperation();

  const statusConfig = {
    [GcodeHarvestStatus.Running]: { color: 'blue', icon: PlayIcon, label: 'Running', bgColor: 'bg-blue-50', textColor: 'text-blue-700' },
    [GcodeHarvestStatus.Completed]: { color: 'green', icon: CheckCircleIcon, label: 'Completed', bgColor: 'bg-green-50', textColor: 'text-green-700' },
    [GcodeHarvestStatus.Failed]: { color: 'red', icon: ExclamationCircleIcon, label: 'Failed', bgColor: 'bg-red-50', textColor: 'text-red-700' },
    [GcodeHarvestStatus.Cancelled]: { color: 'gray', icon: XCircleIcon, label: 'Cancelled', bgColor: 'bg-gray-50', textColor: 'text-gray-700' }
  };

  const config = statusConfig[operation.status];
  const progress = operation.filesProcessed / Math.max(operation.filesFound, 1) * 100;

  const handleCancel = () => {
    if (!window.confirm('Are you sure you want to cancel this harvest operation? This action cannot be undone.')) {
      return;
    }
    cancelMutation.mutate(operation.id, {
      onSuccess: () => {
        toast.success('Harvest operation cancelled successfully');
        onOperationUpdate?.();
      },
      onError: (error) => {
        toast.error(error instanceof Error ? error.message : 'Failed to cancel harvest operation');
      }
    });
  };

  return (
    <div
      className={
        'bg-white rounded-lg shadow border border-gray-200 mb-6' +
        (onViewDetails ? ' cursor-pointer' : '')
      }
      onClick={onViewDetails ? () => onViewDetails(operation) : undefined}
    >
      <div className="flex items-center justify-between p-4 border-b border-gray-100">
        <div className="flex items-center space-x-3">
          <config.icon className={`w-6 h-6 ${config.textColor}`} />
          <div>
            <h2 className="text-lg font-semibold text-gray-900">Harvest Progress</h2>
            <p className="text-sm text-gray-500">{operation.printerName}</p>
          </div>
        </div>
        {operation.status === GcodeHarvestStatus.Running && hasPermission('gcode_harvest', 'execute') && (
          <button
            onClick={handleCancel}
            disabled={cancelMutation.isPending}
            className="inline-flex items-center px-3 py-1.5 text-sm font-medium text-white bg-red-600 border border-transparent rounded-md hover:bg-red-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-red-500 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {cancelMutation.isPending ? (
              <>
                <StopIcon className="w-4 h-4 mr-2 animate-pulse" />
                Cancelling...
              </>
            ) : (
              <>
                <StopIcon className="w-4 h-4 mr-2" />
                Cancel
              </>
            )}
          </button>
        )}
      </div>
      <div className="p-4 space-y-4">
        <div className={`p-3 rounded-lg ${config.bgColor}`}>
          <div className="flex items-center justify-between mb-2">
            <div className="flex items-center space-x-2">
              <config.icon className={`w-5 h-5 ${config.textColor}`} />
              <span className={`font-medium ${config.textColor}`}>{config.label}</span>
            </div>
            <span className="text-sm text-gray-600">
              {operation.status === GcodeHarvestStatus.Running && `${Math.round(progress)}% complete`}
            </span>
          </div>
          {operation.status === GcodeHarvestStatus.Running && (
            <div className="space-y-2">
              <div className="flex items-center justify-between text-sm">
                <span>{operation.filesProcessed} / {operation.filesFound} files processed</span>
                <span>{Math.round(progress)}%</span>
              </div>
              <div className="w-full bg-gray-200 rounded-full h-2">
                <div
                  className={`bg-${config.color}-600 h-2 rounded-full transition-all duration-300`}
                  style={{ width: `${Math.min(progress, 100)}%` }}
                />
              </div>
              {/* Per-file progress bars */}
              {perFileProgress && Object.keys(perFileProgress).length > 0 ? (
                <div className="mt-4 space-y-2">
                  <div className="text-xs text-gray-500 font-medium mb-1">Per-file Progress</div>
                  {Object.values(perFileProgress).map(fp => (
                    <div key={fp.fileName} className="mb-1">
                      <div className="flex items-center justify-between text-xs mb-0.5">
                        <span className="truncate max-w-[60%]" title={fp.fileName}>{fp.fileName}</span>
                        <span className="ml-2">{Math.round(fp.percent)}%</span>
                        <span className="ml-2 text-gray-400">{fp.status}</span>
                      </div>
                      <div className="w-full bg-gray-100 rounded-full h-1">
                        <div
                          className={`h-1 rounded-full transition-all duration-300 ${
                            fp.status === 'completed' ? 'bg-green-500' :
                            fp.status === 'errored' ? 'bg-red-500' :
                            fp.status === 'skipped' ? 'bg-yellow-400' :
                            'bg-blue-500'
                          }`}
                          style={{ width: `${Math.min(fp.percent, 100)}%` }}
                        />
                      </div>
                    </div>
                  ))}
                </div>
              ) : (
                <div className="mt-4 text-xs text-red-500">
                  <strong>Debug:</strong> No per-file progress data.<br />
                  <pre className="whitespace-pre-wrap break-all bg-gray-100 p-2 rounded">
                    {JSON.stringify(perFileProgress, null, 2)}
                  </pre>
                </div>
              )}
            </div>
          )}
        </div>
        {/* Statistics */}
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
          <div className="bg-gray-50 p-4 rounded-lg">
            <div className="text-2xl font-bold text-gray-900">{operation.filesFound}</div>
            <div className="text-sm text-gray-600">Files Found</div>
          </div>
          <div className="bg-gray-50 p-4 rounded-lg">
            <div className="text-2xl font-bold text-green-600">{operation.filesAdded}</div>
            <div className="text-sm text-gray-600">Files Added</div>
          </div>
          <div className="bg-gray-50 p-4 rounded-lg">
            <div className="text-2xl font-bold text-yellow-600">{operation.filesSkipped}</div>
            <div className="text-sm text-gray-600">Files Skipped</div>
          </div>
          <div className="bg-gray-50 p-4 rounded-lg">
            <div className="text-2xl font-bold text-red-600">{operation.filesErrored}</div>
            <div className="text-sm text-gray-600">Files Errored</div>
          </div>
        </div>
        {/* Additional Details */}
        <div className="space-y-2">
          <div className="flex justify-between items-center">
            <span className="text-sm font-medium text-gray-600">Started:</span>
            <span className="text-sm text-gray-900">
              {formatDistanceToNow(parseApiDateTimeValue(operation.startedAt), { addSuffix: true })}
            </span>
          </div>
          {operation.completedAt && (
            <div className="flex justify-between items-center">
              <span className="text-sm font-medium text-gray-600">Completed:</span>
              <span className="text-sm text-gray-900">
                {formatDistanceToNow(parseApiDateTimeValue(operation.completedAt), { addSuffix: true })}
              </span>
            </div>
          )}
          <div className="flex justify-between items-center">
            <span className="text-sm font-medium text-gray-600">Duration:</span>
            <span className="text-sm text-gray-900">
              {formatDuration(operation.startedAt, operation.completedAt)}
            </span>
          </div>
          {operation.totalSizeBytes > 0 && (
            <div className="flex justify-between items-center">
              <span className="text-sm font-medium text-gray-600">Total Size:</span>
              <span className="text-sm text-gray-900">{formatBytes(operation.totalSizeBytes)}</span>
            </div>
          )}
          {operation.duplicatesSkipped > 0 && (
            <div className="flex justify-between items-center">
              <span className="text-sm font-medium text-gray-600">Duplicates Skipped:</span>
              <span className="text-sm text-gray-900">{operation.duplicatesSkipped}</span>
            </div>
          )}
        </div>
        {/* Harvest Options */}
        {operation.options && (
          <div className="border-t border-gray-200 pt-4">
            <h4 className="text-sm font-medium text-gray-900 mb-3">Harvest Options</h4>
            <div className="space-y-2 text-sm">
              <div className="flex justify-between">
                <span className="text-gray-600">Include Subfolders:</span>
                <span className="text-gray-900">{operation.options.includeSubfolders ? 'Yes' : 'No'}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-gray-600">File Types:</span>
                <span className="text-gray-900">{operation.options.fileTypes?.join(', ') || 'All'}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-gray-600">Min File Size:</span>
                <span className="text-gray-900">{formatBytes(operation.options.minFileSize)}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-gray-600">Duplicate Handling:</span>
                <span className="text-gray-900 capitalize">{operation.options.duplicateHandling}</span>
              </div>
            </div>
          </div>
        )}
        {/* Error message */}
        {operation.status === GcodeHarvestStatus.Failed && operation.error && (
          <div className="border-t border-gray-200 pt-4">
            <h4 className="text-sm font-medium text-red-900 mb-2">Error Details</h4>
            <div className="bg-red-50 border border-red-200 rounded p-3 text-sm text-red-700">
              {operation.error}
            </div>
          </div>
        )}
      </div>
    </div>
  );
};
