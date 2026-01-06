import React, { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Clock,
  Package,
  ArrowUp,
  ArrowDown,
  MoreHorizontal,
  AlertCircle
} from 'lucide-react';
import { DeleteIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
// ConfirmationModal not used in JobCard
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
    try {
      await removeMutation.mutateAsync(job.id);
      setShowRemoveConfirmation(false);
    } catch (error) {
      console.error('Failed to remove job:', error);
      setShowRemoveConfirmation(false);
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
      bg-white rounded-lg border p-4 shadow-sm hover:shadow-md transition-shadow overflow-hidden flex flex-col min-h-0
      ${isActive ? 'border-pf-accent bg-pf-bg-2' : ''}
      ${isCompleted ? 'border-gray-200 opacity-75' : ''}
      hover:bg-pf-bg-secondary transition-colors
    `}>
      <div className="flex items-start justify-between mb-3">
        <div className="flex-1 min-w-0">
          <div className="flex items-center space-x-2 mb-1">
            <h3 className="font-medium text-pf-text-primary truncate">{job.gcodeFileName}</h3>
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
              <Button
                onClick={() => setShowActions(!showActions)}
                variant="subtle"
                size="sm"
                disabled={removeMutation.isPending || priorityMutation.isPending}
                aria-label="Toggle job actions menu"
                title="Job actions"
                className="!p-1"
                iconCenter={<MoreHorizontal className="w-4 h-4 text-gray-400" />}
              ></Button>

              {showActions && (
                <div className="absolute right-0 top-8 bg-white border border-gray-200 rounded-lg shadow-lg py-1 z-10 min-w-32">
                  <Button
                    onClick={() => handleChangePriority(3)}
                    variant="subtle"
                    size="sm"
                    disabled={job.priority === 3}
                    aria-label="Set priority to Urgent"
                    title="Set priority to Urgent"
                    className="w-full text-left flex items-center gap-2"
                    iconLeft={<ArrowUp className="w-4 h-4 text-red-500" />}
                  >Urgent</Button>
                  <Button
                    onClick={() => handleChangePriority(2)}
                    variant="subtle"
                    size="sm"
                    disabled={job.priority === 2}
                    aria-label="Set priority to High"
                    title="Set priority to High"
                    className="w-full text-left flex items-center gap-2"
                    iconLeft={<ArrowUp className="w-4 h-4 text-orange-500" />}
                  >High</Button>
                  <Button
                    onClick={() => handleChangePriority(1)}
                    variant="subtle"
                    size="sm"
                    disabled={job.priority === 1}
                    aria-label="Set priority to Normal"
                    title="Set priority to Normal"
                    className="w-full text-left flex items-center"
                  >
                    Normal
                  </Button>
                  <Button
                    onClick={() => handleChangePriority(0)}
                    variant="subtle"
                    size="sm"
                    disabled={job.priority === 0}
                    aria-label="Set priority to Low"
                    title="Set priority to Low"
                    className="w-full text-left flex items-center gap-2"
                    iconLeft={<ArrowDown className="w-4 h-4 text-gray-500" />}
                  >Low</Button>
                  <hr className="my-1" />
                  <Button
                    onClick={handleRemoveJob}
                    variant="danger"
                    size="sm"
                    aria-label="Remove job from queue"
                    title="Remove job"
                    className="w-full text-left flex items-center gap-2"
                    iconLeft={<DeleteIcon className="w-4 h-4" />}
                  >Remove</Button>
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