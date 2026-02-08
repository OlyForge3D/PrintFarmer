/**
 * RecentPrintsWidget Component
 * 
 * Dashboard widget showing recent print history.
 * Uses the common DashboardWidget for consistent styling.
 */

import React from 'react';
import { DashboardWidget } from '@/common/components/DashboardWidget';
import { TrendingUpIcon } from '@/common/components/icons/MdiIcons';
import { usePrinters, usePrinterHistory } from '@/common/hooks/useApi';

export interface RecentPrintsWidgetProps {
  /** Maximum prints to display */
  maxPrints?: number;
  /** Additional CSS classes */
  className?: string;
}

/**
 * Dashboard widget showing recent print history
 */
export function RecentPrintsWidget({ maxPrints = 5, className = '' }: RecentPrintsWidgetProps) {
  const { data: printers } = usePrinters();
  
  // Fetch history for the first printer
  const firstPrinterId = printers?.[0]?.id;
  const { data: recentHistory } = usePrinterHistory(
    firstPrinterId || '',
    { limit: maxPrints, order: 'desc' }
  );

  const hasPrints = recentHistory?.jobs && recentHistory.jobs.length > 0;
  const printCount = recentHistory?.jobs?.length ?? 0;

  return (
    <DashboardWidget
      title="Recent Prints"
      icon={TrendingUpIcon}
      iconColorClass={hasPrints ? 'text-green-400' : 'text-pf-text-tertiary'}
      iconBgClass={hasPrints ? 'bg-green-500/20' : 'bg-pf-bg-2'}
      subtitle={
        hasPrints
          ? `${printCount} recent print${printCount !== 1 ? 's' : ''}`
          : 'No print history'
      }
      hasContent={hasPrints}
      collapsible
      storageKey="recent-prints"
      className={className}
      emptyState={
        <div className="text-center py-6">
          <TrendingUpIcon className="h-10 w-10 text-pf-text-tertiary mx-auto mb-2" />
          <p className="text-sm text-pf-text-primary font-medium">No Recent Prints</p>
          <p className="text-xs text-pf-text-tertiary mt-1">Print history will appear here</p>
        </div>
      }
    >
      <div className="space-y-2 max-h-64 overflow-y-auto">
        {recentHistory?.jobs?.slice(0, maxPrints).map((job) => (
          <div key={job.jobId} className="flex items-start justify-between p-3 bg-pf-bg-1 rounded-lg border border-pf-border">
            <div className="flex-1 min-w-0">
              <p className="text-sm font-medium text-pf-text-primary truncate">{job.filename}</p>
              <p className="text-xs text-pf-text-tertiary">
                {job.status === 'Success' ? '✓ Completed' : job.status === 'Failed' ? '✗ Failed' : job.status}
              </p>
            </div>
            <div className="ml-2 text-right">
              <p className="text-xs font-medium text-pf-text-secondary">
                {job.printDuration ? Math.floor((job.printDuration ?? 0) / 60) : 0}m
              </p>
              <span className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium whitespace-nowrap ${
                job.status === 'Success' 
                  ? 'bg-green-500/20 text-green-400' 
                  : job.status === 'Failed' 
                  ? 'bg-red-500/20 text-red-400'
                  : 'bg-pf-border-medium text-pf-text-secondary'
              }`}>
                {job.status}
              </span>
            </div>
          </div>
        ))}
      </div>
    </DashboardWidget>
  );
}
