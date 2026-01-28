/**
 * ActiveJobsWidget Component
 * 
 * Dashboard widget showing active and queued print jobs.
 * Uses the common DashboardWidget for consistent styling.
 */

import React from 'react';
import { DashboardWidget } from '@/common/components/DashboardWidget';
import { PlayIcon, ClockIcon } from '@/common/components/icons/MdiIcons';
import { useJobQueue } from '@/common/hooks/useApi';

export interface ActiveJobsWidgetProps {
  /** Maximum jobs to display */
  maxJobs?: number;
  /** Additional CSS classes */
  className?: string;
}

/**
 * Dashboard widget showing active and queued jobs
 */
export function ActiveJobsWidget({ maxJobs = 5, className = '' }: ActiveJobsWidgetProps) {
  const { data: globalQueue } = useJobQueue(undefined);

  const hasJobs = globalQueue && globalQueue.length > 0;
  const jobCount = globalQueue?.length ?? 0;

  return (
    <DashboardWidget
      title="Active & Queued Jobs"
      icon={PlayIcon}
      iconColorClass={hasJobs ? 'text-blue-400' : 'text-pf-text-tertiary'}
      iconBgClass={hasJobs ? 'bg-blue-500/20' : 'bg-pf-bg-2'}
      subtitle={
        hasJobs
          ? `${jobCount} job${jobCount !== 1 ? 's' : ''} in queue`
          : 'No active jobs'
      }
      hasContent={hasJobs}
      collapsible
      storageKey="active-jobs"
      moreInfoLink="/printQueue"
      moreInfoText="View Queue"
      className={className}
      emptyState={
        <div className="text-center py-6">
          <ClockIcon className="h-10 w-10 text-pf-text-tertiary mx-auto mb-2" />
          <p className="text-sm text-pf-text-primary font-medium">No Jobs in Queue</p>
          <p className="text-xs text-pf-text-tertiary mt-1">Queue a print from the G-code library</p>
        </div>
      }
    >
      <div className="space-y-2 max-h-64 overflow-y-auto">
        {globalQueue?.slice(0, maxJobs).map((item) => (
          <div key={item.job.id} className="flex items-start justify-between p-3 bg-pf-bg-1 rounded-lg border border-pf-border">
            <div className="flex-1 min-w-0">
              <p className="text-sm font-medium text-pf-text-primary truncate">
                {item.gcodeFile?.name ?? item.job.fileName ?? item.job.name ?? 'Unknown Job'}
              </p>
              <p className="text-xs text-pf-text-tertiary">
                Queue Position: {item.job.queuePosition}
                {item.assignedPrinter && ` • ${item.assignedPrinter.name}`}
              </p>
            </div>
            <span className={`ml-2 inline-flex items-center px-2 py-1 rounded-full text-xs font-medium whitespace-nowrap ${
              item.job.status === 'Printing' 
                ? 'bg-green-500/20 text-green-400'
                : 'bg-blue-500/20 text-blue-400'
            }`}>
              {item.job.status}
            </span>
          </div>
        ))}
        {globalQueue && globalQueue.length > maxJobs && (
          <p className="text-xs text-pf-text-tertiary text-center py-2">
            +{globalQueue.length - maxJobs} more in queue
          </p>
        )}
      </div>
    </DashboardWidget>
  );
}
