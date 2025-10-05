import React, { useState, useRef, useEffect } from 'react';
import { Link } from 'react-router-dom';
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
import { useAuth } from '@/contexts/AuthHooks';
import { useCancelHarvestOperation } from '@/hooks/useApi';
import { toast } from 'sonner';
import { parseApiDateTimeValue, formatDuration } from '@/utils/datetime';

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
  const progressRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (progressRef.current) {
      progressRef.current.style.width = `${Math.min(progress, 100)}%`;
    }
  }, [progress]);

  const handleCancel = () => {
    if (!window.confirm('Are you sure you want to cancel this harvest operation? This action cannot be undone.')) {
      return;
    }
    
    cancelMutation.mutate(operation.id, {
      onSuccess: () => {
        toast.success('Harvest operation cancelled successfully');
      },
      onError: (error) => {
        toast.error(error instanceof Error ? error.message : 'Failed to cancel harvest operation');
      }
    });
  };

  return (
    <div className="p-4">
      <div className="flex items-start justify-between">
        <div className="flex items-start space-x-3">
          <div className={`flex-shrink-0 w-8 h-8 rounded-full bg-${config.color}-100 flex items-center justify-center`}>
            <config.icon className={`w-4 h-4 text-${config.color}-600`} />
          </div>
          
          <div className="flex-1">
            <div className="flex items-center space-x-2">
              <h4 className="font-medium text-gray-900">
                Harvest from {operation.printerName}
              </h4>
              <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-${config.color}-100 text-${config.color}-800`}>
                {config.label}
              </span>
            </div>
            
            <div className="mt-1 text-sm text-gray-500">
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
                <div className="w-full bg-gray-200 rounded-full h-1.5">
                  <div
                    ref={progressRef}
                    className="bg-blue-600 h-1.5 rounded-full transition-all duration-300"
                  />
                </div>
              </div>
            )}

            {/* Results summary */}
            {operation.status === GcodeHarvestStatus.Completed && (
              <div className="mt-2 flex items-center space-x-4 text-sm">
                <span className="text-gray-600">
                  <span className="font-medium text-green-600">{operation.filesProcessed}</span> files harvested
                </span>
                
                {operation.totalSizeBytes && (
                  <span className="text-gray-600">
                    <span className="font-medium">{formatBytes(operation.totalSizeBytes)}</span> total size
                  </span>
                )}
                
                {operation.duplicatesSkipped > 0 && (
                  <span className="text-gray-600">
                    {operation.duplicatesSkipped} duplicates skipped
                  </span>
                )}
              </div>
            )}

            {/* Error message */}
            {operation.status === GcodeHarvestStatus.Failed && operation.error && (
              <div className="mt-2 p-2 bg-red-50 border border-red-200 rounded text-sm text-red-700">
                {operation.error}
              </div>
            )}
          </div>
        </div>

        <div className="flex items-center space-x-2">
          {operation.status === GcodeHarvestStatus.Running && onViewDetails && (
            <button
              onClick={() => onViewDetails(operation)}
              className="text-sm text-blue-600 hover:text-blue-800"
            >
              View Details
            </button>
          )}
          
          {operation.status === GcodeHarvestStatus.Completed && hasPermission('gcode_harvest', 'read') && (
            <Link
              to={`/files?harvest=${operation.id}`}
              className="text-sm text-blue-600 hover:text-blue-800"
            >
              View Files
            </Link>
          )}
          
          {operation.status === GcodeHarvestStatus.Running && hasPermission('gcode_harvest', 'execute') && (
            <button
              onClick={handleCancel}
              disabled={cancelMutation.isPending}
              className="text-sm text-red-600 hover:text-red-800 disabled:text-red-400 disabled:cursor-not-allowed"
              title="Cancel harvest operation"
            >
              {cancelMutation.isPending ? (
                <span className="flex items-center">
                  <StopIcon className="w-4 h-4 mr-1 animate-spin" />
                  Cancelling...
                </span>
              ) : (
                <span className="flex items-center">
                  <StopIcon className="w-4 h-4 mr-1" />
                  Cancel
                </span>
              )}
            </button>
          )}
          
          <button
            onClick={() => setShowDetails(!showDetails)}
            className="text-gray-400 hover:text-gray-600"
            title="Toggle details"
          >
            <ChevronDownIcon className={`w-4 h-4 transition-transform ${showDetails ? 'rotate-180' : ''}`} />
          </button>
        </div>
      </div>

      {/* Expanded details */}
      {showDetails && (
        <div className="mt-4 pt-4 border-t border-gray-200">
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm">
            <div>
              <span className="text-gray-600">Operation ID:</span>
              <div className="font-mono text-xs text-gray-900 mt-1">
                {operation.id.substring(0, 8)}...
              </div>
            </div>
            
            <div>
              <span className="text-gray-600">File Types:</span>
              <div className="font-medium text-gray-900 mt-1">
                {operation.options?.fileTypes?.join(', ') || 'All'}
              </div>
            </div>
            
            <div>
              <span className="text-gray-600">Include Subfolders:</span>
              <div className="font-medium text-gray-900 mt-1">
                {operation.options?.includeSubfolders ? 'Yes' : 'No'}
              </div>
            </div>
            
            <div>
              <span className="text-gray-600">Min File Size:</span>
              <div className="font-medium text-gray-900 mt-1">
                {operation.options?.minFileSize ? formatBytes(operation.options.minFileSize) : 'None'}
              </div>
            </div>
          </div>

          {operation.filesPaths && operation.filesPaths.length > 0 && (
            <div className="mt-4">
              <span className="text-sm text-gray-600">Sample Files:</span>
              <div className="mt-1 text-xs font-mono space-y-1">
                {operation.filesPaths.slice(0, 3).map((path, i) => (
                  <div key={i} className="text-gray-700 truncate">
                    {path}
                  </div>
                ))}
                {operation.filesPaths.length > 3 && (
                  <div className="text-gray-500">
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