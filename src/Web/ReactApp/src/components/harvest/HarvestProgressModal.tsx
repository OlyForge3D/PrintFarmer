import React from 'react';
import { formatDistanceToNow } from 'date-fns';
import { 
  XMarkIcon,
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

interface HarvestProgressModalProps {
  isOpen: boolean;
  onClose: () => void;
  operation: GcodeHarvestOperation;
  onOperationUpdate?: () => void;
}

// Utility function to format bytes
const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 Bytes';
  const k = 1024;
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

export const HarvestProgressModal: React.FC<HarvestProgressModalProps> = ({
  isOpen,
  onClose,
  operation,
  onOperationUpdate
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

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 z-50 overflow-y-auto">
      <div className="flex min-h-screen items-center justify-center p-4">
        <div className="fixed inset-0 bg-black bg-opacity-50" onClick={onClose} />
        
        <div className="relative bg-white rounded-lg shadow-xl max-w-2xl w-full max-h-[80vh] overflow-hidden">
          {/* Header */}
          <div className="flex items-center justify-between p-6 border-b border-gray-200">
            <div className="flex items-center space-x-3">
              <config.icon className={`w-6 h-6 ${config.textColor}`} />
              <div>
                <h2 className="text-xl font-semibold text-gray-900">Harvest Progress</h2>
                <p className="text-sm text-gray-500">
                  {operation.printerName}
                </p>
              </div>
            </div>
            
            <button
              onClick={onClose}
              className="text-gray-400 hover:text-gray-600"
              title="Close"
            >
              <XMarkIcon className="w-6 h-6" />
            </button>
          </div>

          {/* Content */}
          <div className="p-6 space-y-6 overflow-y-auto max-h-[60vh]">
            {/* Status and Progress */}
            <div className={`p-4 rounded-lg ${config.bgColor}`}>
              <div className="flex items-center justify-between mb-3">
                <div className="flex items-center space-x-2">
                  <config.icon className={`w-5 h-5 ${config.textColor}`} />
                  <span className={`font-medium ${config.textColor}`}>{config.label}</span>
                </div>
                <span className="text-sm text-gray-600">
                  {operation.status === GcodeHarvestStatus.Running && (
                    `${Math.round(progress)}% complete`
                  )}
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
                </div>
              )}
            </div>

            {/* Statistics */}
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
              <div className="bg-gray-50 p-4 rounded-lg">
                <div className="text-2xl font-bold text-gray-900">
                  {operation.filesFound}
                </div>
                <div className="text-sm text-gray-600">Files Found</div>
              </div>
              
              <div className="bg-gray-50 p-4 rounded-lg">
                <div className="text-2xl font-bold text-green-600">
                  {operation.filesAdded}
                </div>
                <div className="text-sm text-gray-600">Files Added</div>
              </div>
              
              <div className="bg-gray-50 p-4 rounded-lg">
                <div className="text-2xl font-bold text-yellow-600">
                  {operation.filesSkipped}
                </div>
                <div className="text-sm text-gray-600">Files Skipped</div>
              </div>
              
              <div className="bg-gray-50 p-4 rounded-lg">
                <div className="text-2xl font-bold text-red-600">
                  {operation.filesErrored}
                </div>
                <div className="text-sm text-gray-600">Files Errored</div>
              </div>
            </div>

            {/* Additional Details */}
            <div className="space-y-4">
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
                  <span className="text-sm text-gray-900">
                    {formatBytes(operation.totalSizeBytes)}
                  </span>
                </div>
              )}

              {operation.duplicatesSkipped > 0 && (
                <div className="flex justify-between items-center">
                  <span className="text-sm font-medium text-gray-600">Duplicates Skipped:</span>
                  <span className="text-sm text-gray-900">
                    {operation.duplicatesSkipped}
                  </span>
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

          {/* Footer Actions */}
          <div className="flex items-center justify-end space-x-3 p-6 border-t border-gray-200 bg-gray-50">
            {operation.status === GcodeHarvestStatus.Running && hasPermission('gcode_harvest', 'execute') && (
              <button
                onClick={handleCancel}
                disabled={cancelMutation.isPending}
                className="inline-flex items-center px-4 py-2 text-sm font-medium text-white bg-red-600 border border-transparent rounded-md hover:bg-red-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-red-500 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {cancelMutation.isPending ? (
                  <>
                    <StopIcon className="w-4 h-4 mr-2 animate-pulse" />
                    Cancelling...
                  </>
                ) : (
                  <>
                    <StopIcon className="w-4 h-4 mr-2" />
                    Cancel Operation
                  </>
                )}
              </button>
            )}
            
            <button
              onClick={onClose}
              className="inline-flex items-center px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500"
            >
              Close
            </button>
          </div>
        </div>
      </div>
    </div>
  );
};