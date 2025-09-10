import React, { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { 
  Clock, 
  Package, 
  ArrowUp, 
  ArrowDown, 
  Trash2, 
  MoreHorizontal, 
  AlertCircle 
} from 'lucide-react';
import { PrintJob, queueService } from '@/services/queueService';

interface JobCardProps {
  job: PrintJob;
  showPrinterInfo?: boolean;
  onJobUpdate?: () => void;
}

export const JobCard: React.FC<JobCardProps> = ({ 
  job, 
  showPrinterInfo = false,
  onJobUpdate 
}) => {
  const [showActions, setShowActions] = useState(false);
  const queryClient = useQueryClient();

  const removeMutation = useMutation({
    mutationFn: queueService.removeJobFromQueue,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['queue-overview'] });
      queryClient.invalidateQueries({ queryKey: ['printer-queue'] });
      onJobUpdate?.();
    }
  });

  const priorityMutation = useMutation({
    mutationFn: ({ jobId, priority }: { jobId: string; priority: number }) =>
      queueService.updateJobPriority(jobId, priority),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['queue-overview'] });
      queryClient.invalidateQueries({ queryKey: ['printer-queue'] });
      onJobUpdate?.();
    }
  });

  const handleRemoveJob = async () => {
    if (window.confirm('Are you sure you want to remove this job from the queue?')) {
      try {
        await removeMutation.mutateAsync(job.id);
      } catch (error) {
        console.error('Failed to remove job:', error);
        alert('Failed to remove job. Please try again.');
      }
    }
  };

  const handleChangePriority = async (newPriority: number) => {
    try {
      await priorityMutation.mutateAsync({ jobId: job.id, priority: newPriority });
    } catch (error) {
      console.error('Failed to update priority:', error);
      alert('Failed to update priority. Please try again.');
    }
  };

  const canModify = ['Queued', 'Assigned'].includes(job.status);
  const isActive = ['Starting', 'Printing'].includes(job.status);
  const isCompleted = ['Completed', 'Failed', 'Cancelled'].includes(job.status);

  return (
    <div className={`
      bg-white rounded-lg border p-4 shadow-sm hover:shadow-md transition-shadow
      ${isActive ? 'border-blue-300 bg-blue-50/50' : ''}
      ${isCompleted ? 'border-gray-200 opacity-75' : ''}
    `}>
      <div className="flex items-start justify-between mb-3">
        <div className="flex-1 min-w-0">
          <div className="flex items-center space-x-2 mb-1">
            <h3 className="font-medium text-gray-900 truncate">{job.gcodeFileName}</h3>
            <span className={`
              inline-flex items-center px-2 py-1 rounded-full text-xs font-medium
              ${queueService.getStatusColor(job.status)}
            `}>
              {job.status}
            </span>
          </div>
          
          {showPrinterInfo && job.assignedPrinterName && (
            <p className="text-sm text-gray-600 mb-1">
              → {job.assignedPrinterName}
            </p>
          )}

          <div className="flex items-center space-x-4 text-sm text-gray-500">
            {job.queuePosition > 0 && (
              <span className="flex items-center">
                <span className="w-4 h-4 bg-yellow-100 text-yellow-800 rounded-full text-xs flex items-center justify-center mr-1">
                  {job.queuePosition}
                </span>
                Position
              </span>
            )}
            
            <span className={`
              inline-flex items-center px-2 py-1 rounded-full text-xs
              ${queueService.getPriorityColor(job.priority)}
            `}>
              {queueService.getPriorityLabel(job.priority)}
            </span>
          </div>
        </div>

        <div className="flex items-center space-x-2">
          {isActive && (
            <div className="w-2 h-2 bg-blue-500 rounded-full animate-pulse"></div>
          )}
          
          {canModify && (
            <div className="relative">
              <button
                onClick={() => setShowActions(!showActions)}
                className="p-1 hover:bg-gray-100 rounded"
                disabled={removeMutation.isPending || priorityMutation.isPending}
                aria-label="Toggle job actions menu"
                title="Job actions"
              >
                <MoreHorizontal className="w-4 h-4 text-gray-400" />
              </button>

              {showActions && (
                <div className="absolute right-0 top-8 bg-white border border-gray-200 rounded-lg shadow-lg py-1 z-10 min-w-32">
                  <button
                    onClick={() => handleChangePriority(3)}
                    className="w-full text-left px-3 py-2 text-sm hover:bg-gray-50 flex items-center"
                    disabled={job.priority === 3}
                    aria-label="Set priority to Urgent"
                    title="Set priority to Urgent"
                  >
                    <ArrowUp className="w-4 h-4 mr-2 text-red-500" />
                    Urgent
                  </button>
                  <button
                    onClick={() => handleChangePriority(2)}
                    className="w-full text-left px-3 py-2 text-sm hover:bg-gray-50 flex items-center"
                    disabled={job.priority === 2}
                    aria-label="Set priority to High"
                    title="Set priority to High"
                  >
                    <ArrowUp className="w-4 h-4 mr-2 text-orange-500" />
                    High
                  </button>
                  <button
                    onClick={() => handleChangePriority(1)}
                    className="w-full text-left px-3 py-2 text-sm hover:bg-gray-50 flex items-center"
                    disabled={job.priority === 1}
                    aria-label="Set priority to Normal"
                    title="Set priority to Normal"
                  >
                    Normal
                  </button>
                  <button
                    onClick={() => handleChangePriority(0)}
                    className="w-full text-left px-3 py-2 text-sm hover:bg-gray-50 flex items-center"
                    disabled={job.priority === 0}
                    aria-label="Set priority to Low"
                    title="Set priority to Low"
                  >
                    <ArrowDown className="w-4 h-4 mr-2 text-gray-500" />
                    Low
                  </button>
                  <hr className="my-1" />
                  <button
                    onClick={handleRemoveJob}
                    className="w-full text-left px-3 py-2 text-sm hover:bg-gray-50 text-red-600 flex items-center"
                    aria-label="Remove job from queue"
                    title="Remove job"
                  >
                    <Trash2 className="w-4 h-4 mr-2" />
                    Remove
                  </button>
                </div>
              )}
            </div>
          )}
        </div>
      </div>

      <div className="grid grid-cols-2 gap-4 text-sm">
        {job.estimatedPrintTime && (
          <div className="flex items-center text-gray-600">
            <Clock className="w-4 h-4 mr-2" />
            {queueService.formatDuration(job.estimatedPrintTime)}
          </div>
        )}
        
        {job.estimatedFilamentUsage && (
          <div className="flex items-center text-gray-600">
            <Package className="w-4 h-4 mr-2" />
            {queueService.formatFilamentUsage(job.estimatedFilamentUsage)}
          </div>
        )}

        {job.requiredMaterialType && (
          <div className="text-gray-600">
            Material: {job.requiredMaterialType}
          </div>
        )}

        {job.requiredNozzleDiameter && (
          <div className="text-gray-600">
            Nozzle: {job.requiredNozzleDiameter}mm
          </div>
        )}
      </div>

      {job.failureReason && (
        <div className="mt-3 p-2 bg-red-50 border border-red-200 rounded-md">
          <div className="flex items-center text-red-800 text-sm">
            <AlertCircle className="w-4 h-4 mr-2" />
            {job.failureReason}
          </div>
        </div>
      )}

      {job.actualStartTime && (
        <div className="mt-3 text-xs text-gray-500">
          Started: {new Date(job.actualStartTime).toLocaleString()}
        </div>
      )}

      {job.actualEndTime && (
        <div className="mt-1 text-xs text-gray-500">
          Completed: {new Date(job.actualEndTime).toLocaleString()}
        </div>
      )}
    </div>
  );
};