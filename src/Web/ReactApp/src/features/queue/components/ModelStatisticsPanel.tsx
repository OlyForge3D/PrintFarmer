import { ModelStats } from "./ModelFilteredJobsTab";

interface ModelStatisticsPanelProps {
  stats: ModelStats[];
  isLoading: boolean;
}

/**
 * ModelStatisticsPanel Component
 *
 * Displays summary statistics for all models:
 * - Total jobs across all models
 * - Number of models being used
 * - Overall average wait time
 * - Busiest model by queue
 * - Most busy model by printing
 */
export default function ModelStatisticsPanel({
  stats,
  isLoading,
}: ModelStatisticsPanelProps) {
  const totalJobs = stats.reduce((sum, s) => sum + s.totalCount, 0);
  const totalQueued = stats.reduce((sum, s) => sum + s.queuedCount, 0);
  const totalPrinting = stats.reduce((sum, s) => sum + s.printingCount, 0);
  const totalPaused = stats.reduce((sum, s) => sum + s.pausedCount, 0);

  const overallAverageWaitTime =
    stats.length > 0
      ? Math.round(
          (stats.reduce((sum, s) => sum + s.averageWaitTimeMinutes, 0) /
            stats.length) *
            10
        ) / 10
      : 0;

  const busiestQueueModel =
    stats.length > 0
      ? stats.reduce((max, s) =>
          s.queuedCount > max.queuedCount ? s : max
        )
      : null;

  const busiestPrintingModel =
    stats.length > 0
      ? stats.reduce((max, s) =>
          s.printingCount > max.printingCount ? s : max
        )
      : null;

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
      <h3 className="text-pf-text-primary font-semibold mb-4">Queue Overview</h3>

      <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 gap-3">
        {/* Total Jobs */}
        <div className="bg-pf-bg-0 border border-pf-border rounded-sm p-3">
          <div className="text-pf-text-secondary text-xs font-medium">
            Total Jobs
          </div>
          <div className="text-2xl font-bold text-pf-text-primary">
            {isLoading ? "..." : totalJobs}
          </div>
        </div>

        {/* Active Models */}
        <div className="bg-pf-bg-0 border border-pf-border rounded-sm p-3">
          <div className="text-pf-text-secondary text-xs font-medium">
            Models
          </div>
          <div className="text-2xl font-bold text-pf-text-primary">
            {isLoading ? "..." : stats.length}
          </div>
        </div>

        {/* Average Wait Time */}
        <div className="bg-pf-bg-0 border border-pf-border rounded-sm p-3">
          <div className="text-pf-text-secondary text-xs font-medium">
            Avg Wait
          </div>
          <div className="text-2xl font-bold text-pf-warning">
            {isLoading ? "..." : `${overallAverageWaitTime}m`}
          </div>
        </div>

        {/* Busiest Queue */}
        <div className="bg-pf-bg-0 border border-pf-border rounded-sm p-3">
          <div className="text-pf-text-secondary text-xs font-medium">
            Largest Queue
          </div>
          <div className="text-lg font-bold text-pf-info">
            {isLoading ? "..." : busiestQueueModel ? busiestQueueModel.queuedCount : 0}
          </div>
          <div className="text-xs text-pf-text-secondary truncate">
            {isLoading ? "..." : busiestQueueModel?.name || "N/A"}
          </div>
        </div>

        {/* Most Printing */}
        <div className="bg-pf-bg-0 border border-pf-border rounded-sm p-3">
          <div className="text-pf-text-secondary text-xs font-medium">
            Most Printing
          </div>
          <div className="text-lg font-bold text-pf-success">
            {isLoading ? "..." : busiestPrintingModel ? busiestPrintingModel.printingCount : 0}
          </div>
          <div className="text-xs text-pf-text-secondary truncate">
            {isLoading ? "..." : busiestPrintingModel?.name || "N/A"}
          </div>
        </div>
      </div>

      {/* Status Summary Row */}
      <div className="flex flex-wrap gap-2 mt-4 pt-4 border-t border-pf-border">
        <span className="text-pf-text-secondary text-sm">
          <span className="font-medium text-pf-info">📊 {totalQueued}</span>{" "}
          Queued
        </span>
        <span className="text-pf-text-secondary text-sm">
          <span className="font-medium text-pf-success">▶ {totalPrinting}</span>{" "}
          Printing
        </span>
        <span className="text-pf-text-secondary text-sm">
          <span className="font-medium text-pf-warning">⏸ {totalPaused}</span>{" "}
          Paused
        </span>
      </div>
    </div>
  );
}
