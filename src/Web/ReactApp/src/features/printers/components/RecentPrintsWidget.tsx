/**
 * RecentPrintsWidget Component
 * 
 * Dashboard widget showing recent print history across all printers.
 * Uses the queue analytics history endpoint for cross-printer results.
 */

import { useQuery } from '@tanstack/react-query';
import { DashboardWidget } from '@/common/components/DashboardWidget';
import { TrendingUpIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';

export interface RecentPrintsWidgetProps {
  /** Maximum prints to display */
  maxPrints?: number;
  /** Additional CSS classes */
  className?: string;
}

/**
 * Dashboard widget showing recent print history across all printers
 */
export function RecentPrintsWidget({ maxPrints = 5, className = '' }: RecentPrintsWidgetProps) {
  // Fetch recent completed/failed/cancelled jobs across ALL printers
  const { data: historyData } = useQuery({
    queryKey: ['queue-history-recent', maxPrints],
    queryFn: () => apiClient.getAnalyticsQueueHistory(maxPrints, 0, 'newest'),
    staleTime: 30000,
  });

  const entries = historyData?.entries ?? [];
  const hasPrints = entries.length > 0;
  const printCount = entries.length;

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
        {entries.slice(0, maxPrints).map((entry) => {
          const statusLabel = entry.status === 'Completed' ? '✓ Completed'
            : entry.status === 'Failed' ? '✗ Failed'
            : entry.status === 'Cancelled' ? '⊘ Cancelled'
            : entry.status;
          const durationMin = entry.actualPrintTimeSeconds
            ? Math.floor(entry.actualPrintTimeSeconds / 60)
            : 0;

          return (
            <div key={entry.id} className="flex items-start justify-between p-3 bg-pf-bg-1 rounded-lg border border-pf-border">
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium text-pf-text-primary truncate">{entry.jobName}</p>
                <p className="text-xs text-pf-text-tertiary">
                  {statusLabel} • {entry.printerName}
                </p>
              </div>
              <div className="ml-2 text-right">
                <p className="text-xs font-medium text-pf-text-secondary">
                  {durationMin}m
                </p>
                <span className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium whitespace-nowrap ${
                  entry.status === 'Completed'
                    ? 'bg-green-500/20 text-green-400' 
                    : entry.status === 'Failed' 
                    ? 'bg-red-500/20 text-red-400'
                    : 'bg-pf-border-medium text-pf-text-secondary'
                }`}>
                  {entry.status}
                </span>
              </div>
            </div>
          );
        })}
      </div>
    </DashboardWidget>
  );
}
