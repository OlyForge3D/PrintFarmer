import { HistoryStats } from "./QueueHistoryTab";

interface HistoryStatisticsPanelProps {
  stats: HistoryStats;
  isLoading: boolean;
}

/**
 * HistoryStatisticsPanel Component
 *
 * Displays summary statistics for job history:
 * - Total completed, failed, cancelled jobs
 * - Success rate percentage
 * - Average job duration
 * - Top failure reasons
 */
export default function HistoryStatisticsPanel({
  stats,
  isLoading,
}: HistoryStatisticsPanelProps) {
  // Get top 3 failure reasons
  const topFailureReasons = Object.entries(stats.failureReasons)
    .sort(([, a], [, b]) => b - a)
    .slice(0, 3);

  const total = stats.totalCompleted + stats.totalFailed + stats.totalCancelled;

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4 space-y-4">
      {/* Main Statistics Grid */}
      <div className="grid grid-cols-1 md:grid-cols-5 gap-4">
        {/* Success Rate */}
        <div className="text-center">
          <div className="text-xs font-medium text-pf-text-secondary uppercase mb-1">
            Success Rate
          </div>
          <div className="text-3xl font-bold text-pf-success">{stats.successRate}%</div>
          <div className="text-xs text-pf-text-secondary mt-1">
            {stats.totalCompleted} of {total}
          </div>
        </div>

        {/* Completed Count */}
        <div className="text-center">
          <div className="text-xs font-medium text-pf-text-secondary uppercase mb-1">
            Completed
          </div>
          <div className="text-3xl font-bold text-pf-success">{stats.totalCompleted}</div>
        </div>

        {/* Failed Count */}
        <div className="text-center">
          <div className="text-xs font-medium text-pf-text-secondary uppercase mb-1">
            Failed
          </div>
          <div className="text-3xl font-bold text-pf-error">{stats.totalFailed}</div>
        </div>

        {/* Cancelled Count */}
        <div className="text-center">
          <div className="text-xs font-medium text-pf-text-secondary uppercase mb-1">
            Cancelled
          </div>
          <div className="text-3xl font-bold text-pf-warning">{stats.totalCancelled}</div>
        </div>

        {/* Average Duration */}
        <div className="text-center">
          <div className="text-xs font-medium text-pf-text-secondary uppercase mb-1">
            Avg Duration
          </div>
          <div className="text-3xl font-bold text-pf-text-primary">{stats.averageDurationMinutes}</div>
          <div className="text-xs text-pf-text-secondary mt-1">minutes</div>
        </div>
      </div>

      {/* Top Failure Reasons */}
      {topFailureReasons.length > 0 && (
        <div className="border-t border-pf-border pt-4">
          <div className="text-sm font-medium text-pf-text-primary mb-3">
            Top Failure Reasons
          </div>
          <div className="space-y-2">
            {topFailureReasons.map(([reason, count]) => (
              <div key={reason} className="flex items-center justify-between text-sm">
                <span className="text-pf-text-secondary">{reason}</span>
                <span className="inline-flex items-center gap-1">
                  <span className="bg-pf-error-bg text-pf-error px-2 py-1 rounded-sm text-xs font-medium">
                    {count}
                  </span>
                </span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Loading State */}
      {isLoading && (
        <div className="text-center text-sm text-pf-text-secondary">
          Updating statistics...
        </div>
      )}
    </div>
  );
}
