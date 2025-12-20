import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { ClockIcon, PrinterIcon, AlertCircleIcon, CheckCircleIcon, XCircleIcon } from '@/components/icons/MdiIcons';
import { QueueCardSkeleton } from '@/components/skeletons/QueueCardSkeleton';
import { queueService } from '@/services/queueService';
import { Button } from '@/components/ui';

interface QueueOverview {
  printerId: string;
  printerName: string;
  printerModel: string;
  isAvailable: boolean;
  queuedJobsCount: number;
  currentJobId?: string;
  currentJobName?: string;
  estimatedCompletionTime?: string;
}

const QueueCard: React.FC<{ queue: QueueOverview }> = ({ queue }) => {
  const getStatusIcon = () => {
    if (!queue.isAvailable) {
      return <XCircleIcon className="w-5 h-5 text-red-500" />;
    }
    if (queue.currentJobId) {
      return <div className="w-5 h-5 bg-green-500 rounded-full animate-pulse" />;
    }
    if (queue.queuedJobsCount > 0) {
      return <ClockIcon className="w-5 h-5 text-yellow-500" />;
    }
    return <CheckCircleIcon className="w-5 h-5 text-green-500" />;
  };

  const getStatusText = () => {
    if (!queue.isAvailable) {
      return 'Offline';
    }
    if (queue.currentJobId) {
      return 'Printing';
    }
    if (queue.queuedJobsCount > 0) {
      return `${queue.queuedJobsCount} jobs queued`;
    }
    return 'Ready';
  };

  const formatEstimatedTime = (time?: string) => {
    if (!time) return null;
    const date = new Date(time);
    const now = new Date();
    const diffMs = date.getTime() - now.getTime();
    const diffHours = Math.ceil(diffMs / (1000 * 60 * 60));
    
    if (diffHours < 1) return 'Less than 1 hour';
    if (diffHours === 1) return '1 hour';
    return `${diffHours} hours`;
  };

  return (
    <div className="bg-white rounded-lg shadow-md p-6 border border-gray-200 hover:shadow-lg transition-shadow">
      <div className="flex items-start justify-between mb-4">
        <div className="flex items-center space-x-3">
          <PrinterIcon className="w-6 h-6 text-gray-600" />
          <div>
            <h3 className="font-semibold text-lg text-gray-900">{queue.printerName}</h3>
            <p className="text-sm text-gray-500">{queue.printerModel}</p>
          </div>
        </div>
        <div className="flex items-center space-x-2">
          {getStatusIcon()}
          <span className="text-sm font-medium text-gray-700">{getStatusText()}</span>
        </div>
      </div>

      <div className="space-y-3">
        {queue.currentJobId && (
          <div className="bg-blue-50 rounded-lg p-3">
            <div className="flex items-center justify-between">
              <span className="text-sm font-medium text-blue-900">Current Job</span>
              <div className="w-2 h-2 bg-blue-500 rounded-full animate-pulse"></div>
            </div>
            <p className="text-sm text-blue-700 mt-1">{queue.currentJobName}</p>
          </div>
        )}

        <div className="flex justify-between items-center text-sm">
          <span className="text-gray-600">Queue Length</span>
          <span className="font-medium text-gray-900">{queue.queuedJobsCount} jobs</span>
        </div>

        {queue.estimatedCompletionTime && (
          <div className="flex justify-between items-center text-sm">
            <span className="text-gray-600">Est. Completion</span>
            <span className="font-medium text-gray-900">
              {formatEstimatedTime(queue.estimatedCompletionTime)}
            </span>
          </div>
        )}
      </div>

      <div className="mt-4 pt-4 border-t border-gray-200">
        <Button
          onClick={() => {
            // Navigate to printer queue detail
            window.location.href = `/queue/printer/${queue.printerId}`;
          }}
          variant="secondary"
          className="w-full"
        >
          View Queue Details
        </Button>
      </div>
    </div>
  );
};

export const QueueOverview: React.FC = () => {
  const { data: queues = [], isLoading, error } = useQuery({
    queryKey: ['queue-overview'],
    queryFn: queueService.getQueueOverview,
    refetchInterval: 5000 // Refresh every 5 seconds
  });

  if (isLoading) {
    return (
      <div className="space-y-6" aria-busy="true" aria-label="Loading queue overview">
        <div className="flex items-center justify-between">
          <h2 className="text-2xl font-bold text-gray-900">Print Queue Overview</h2>
          <div className="h-6 w-6 rounded-full bg-gray-200 animate-pulse" />
        </div>
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
          {Array.from({ length: 6 }).map((_, i) => (
            <QueueCardSkeleton key={i} />
          ))}
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="space-y-6">
        <h2 className="text-2xl font-bold text-gray-900">Print Queue Overview</h2>
        <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
          <AlertCircleIcon className="w-8 h-8 text-red-600 mx-auto mb-2" />
          <p className="text-red-800 font-medium">Failed to load queue data</p>
          <p className="text-red-600 text-sm mt-1">Please check your connection and try again</p>
        </div>
      </div>
    );
  }

  const totalQueued = queues.reduce((sum, queue) => sum + queue.queuedJobsCount, 0);
  const activePrinters = queues.filter(q => q.isAvailable).length;
  const printingNow = queues.filter(q => q.currentJobId).length;

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h2 className="text-2xl font-bold text-gray-900">Print Queue Overview</h2>
        <div className="flex items-center space-x-4 text-sm">
          <div className="bg-green-100 text-green-800 px-3 py-1 rounded-full">
            {printingNow} printing
          </div>
          <div className="bg-blue-100 text-blue-800 px-3 py-1 rounded-full">
            {totalQueued} queued
          </div>
          <div className="bg-gray-100 text-gray-800 px-3 py-1 rounded-full">
            {activePrinters} online
          </div>
        </div>
      </div>

      {queues.length === 0 ? (
        <div className="bg-gray-50 border border-gray-200 rounded-lg p-12 text-center">
          <PrinterIcon className="w-12 h-12 text-gray-400 mx-auto mb-4" />
          <h3 className="text-lg font-medium text-gray-900 mb-2">No Printers Available</h3>
          <p className="text-gray-600">Add printers to your farm to start managing print queues</p>
        </div>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
          {queues.map((queue) => (
            <QueueCard key={queue.printerId} queue={queue} />
          ))}
        </div>
      )}
    </div>
  );
};