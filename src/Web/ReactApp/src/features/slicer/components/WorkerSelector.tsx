import React from 'react';
import { WorkerResponse } from '@/types/worker';
import {
  isWorkerAvailable,
  formatWorkerCapacity,
  calculateWorkerUtilization,
  getWorkerStatusColor,
} from '@/types/worker';
import { ServerIcon, ActivityIcon, BatteryIcon } from '@/common/components/icons/MdiIcons';

interface WorkerSelectorProps {
  workers: WorkerResponse[];
  selectedWorkerId?: string;
  onWorkerSelect?: (workerId: string) => void;
  loading?: boolean;
  error?: string | null;
  showCapabilities?: boolean;
  highlightAvailable?: boolean;
}

/**
 * Component for displaying and selecting available slicer workers
 * Shows worker status, capacity, and capabilities
 */
export const WorkerSelector: React.FC<WorkerSelectorProps> = ({
  workers,
  selectedWorkerId,
  onWorkerSelect,
  loading = false,
  error = null,
  showCapabilities = true,
  highlightAvailable = true,
}) => {
  if (loading) {
    return (
      <div className="flex items-center justify-center p-8 text-pf-text-muted">
        <ActivityIcon className="w-5 h-5 mr-2 animate-spin" />
        <span>Loading workers...</span>
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-4 bg-red-50 border border-red-200 rounded-sm text-red-700 text-sm">
        <strong>Error:</strong> {error}
      </div>
    );
  }

  if (workers.length === 0) {
    return (
      <div className="p-8 text-center text-pf-text-muted">
        <ServerIcon className="w-12 h-12 mx-auto mb-2 opacity-50" />
        <p className="text-sm">No workers available</p>
        <p className="text-xs mt-1">Workers matching your capabilities will appear here</p>
      </div>
    );
  }

  return (
    <div className="space-y-2">
      <div className="text-sm text-pf-text-muted mb-3">
        {workers.length} worker{workers.length === 1 ? '' : 's'} available
      </div>
      {workers.map(worker => {
        const available = isWorkerAvailable(worker);
        const utilization = calculateWorkerUtilization(worker);
        const isSelected = selectedWorkerId === worker.id;

        return (
          <div
            data-testid={`worker-card-${worker.id}`}
            key={worker.id}
            onClick={() => onWorkerSelect && onWorkerSelect(worker.id)}
            className={`
              border rounded p-3 cursor-pointer transition-all
              ${isSelected ? 'border-pf-primary bg-pf-accent-bg/5 ring-2 ring-pf-primary/20' : 'border-pf-border hover:border-pf-primary/50'}
              ${!available && highlightAvailable ? 'opacity-60' : ''}
              ${onWorkerSelect ? 'hover:bg-pf-panel-hover' : ''}
            `}
            role="button"
            tabIndex={0}
            aria-label={`Worker ${worker.name}`}
            aria-pressed={isSelected ? 'true' : 'false'}
          >
            <div className="flex items-start justify-between">
              <div className="flex-1">
                <div className="flex items-center gap-2 mb-1">
                  <ServerIcon className="w-4 h-4 text-pf-text-muted" />
                  <span className="font-semibold text-sm">{worker.name}</span>
                  <span
                    className={`text-xs px-2 py-0.5 rounded-full text-white ${getWorkerStatusColor(worker.status)}`}
                  >
                    {worker.status}
                  </span>
                  {available && (
                    <BatteryIcon className="w-3.5 h-3.5 text-green-500" aria-label="Available for jobs" />
                  )}
                </div>

                <div className="text-xs text-pf-text-muted space-y-1">
                  <div className="flex items-center gap-4">
                    <span>
                      <strong>Capacity:</strong> {formatWorkerCapacity(worker)} ({utilization}% used)
                    </span>
                    <span>
                      <strong>Version:</strong> {worker.version}
                    </span>
                  </div>

                  {showCapabilities && Array.isArray(worker.capabilities) && worker.capabilities.length > 0 && (
                    <div className="mt-2">
                      <strong className="text-pf-text-primary">Capabilities:</strong>{' '}
                      <div className="flex flex-wrap gap-1 mt-1">
                        {worker.capabilities.map(cap => (
                          <span
                            key={cap}
                            className="inline-block px-2 py-0.5 bg-pf-panel border border-pf-border rounded-sm text-xs"
                          >
                            {cap}
                          </span>
                        ))}
                      </div>
                    </div>
                  )}

                  <div className="flex items-center gap-4 mt-1 text-xs">
                    <span>
                      <strong>Jobs:</strong> {worker.activeJobs} active, {worker.completedJobs} completed
                    </span>
                    {(worker.averageProcessingTimeSeconds ?? 0) > 0 && (
                      <span>
                        <strong>Avg time:</strong> {Math.round(worker.averageProcessingTimeSeconds ?? 0)}s
                      </span>
                    )}
                  </div>

                  {worker.isDisabled && worker.disabledReason && (
                    <div className="mt-2 text-xs text-red-600">
                      <strong>Disabled:</strong> {worker.disabledReason}
                    </div>
                  )}
                </div>
              </div>

              {isSelected && (
                <div className="ml-2">
                  <div className="w-5 h-5 bg-pf-accent-bg rounded-full flex items-center justify-center">
                    <svg className="w-3 h-3 text-white" fill="currentColor" viewBox="0 0 20 20">
                      <path
                        fillRule="evenodd"
                        d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"
                        clipRule="evenodd"
                      />
                    </svg>
                  </div>
                </div>
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
};

export default WorkerSelector;
