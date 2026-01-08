import { DurationAnalyticsDto, DurationStatsDto } from '@/services/printQueueService';

interface DurationComparisonProps {
  analytics: DurationAnalyticsDto;
}

export function DurationComparison({ analytics }: DurationComparisonProps) {
  const formatDuration = (seconds?: number) => {
    if (!seconds) return 'N/A';
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);

    if (hours > 0) {
      return `${hours}h ${minutes}m`;
    } else {
      return `${minutes}m`;
    }
  };

  const renderMetricCard = (label: string, value: string | number, subtext?: string, isPercentage = false) => (
    <div className="bg-pf-bg-2 border border-pf-border rounded p-4">
      <p className="text-sm text-pf-text-secondary mb-2">{label}</p>
      <div className="flex items-baseline gap-2">
        <p className="text-2xl font-bold text-pf-text-primary">
          {typeof value === 'number' ? value.toFixed(1) : value}
        </p>
        {isPercentage && <p className="text-pf-text-secondary">%</p>}
      </div>
      {subtext && <p className="text-xs text-pf-text-secondary mt-2">{subtext}</p>}
    </div>
  );

  const renderPrinterStats = (printer: DurationStatsDto) => (
    <div key={printer.printerId} className="border border-pf-border rounded-lg p-4 bg-pf-bg-2">
      <h4 className="font-semibold text-pf-text-primary mb-3">{printer.printerName}</h4>

      <div className="grid grid-cols-2 md:grid-cols-4 gap-2 text-sm">
        <div>
          <p className="text-pf-text-secondary">Jobs Completed</p>
          <p className="text-lg font-bold text-pf-text-primary">{printer.totalJobs}</p>
        </div>

        {printer.averageEstimatedSeconds !== undefined && (
          <div>
            <p className="text-pf-text-secondary">Avg Estimated</p>
            <p className="text-lg font-bold text-pf-text-primary">{formatDuration(printer.averageEstimatedSeconds)}</p>
          </div>
        )}

        {printer.averageActualSeconds !== undefined && (
          <div>
            <p className="text-pf-text-secondary">Avg Actual</p>
            <p className="text-lg font-bold text-pf-text-primary">{formatDuration(printer.averageActualSeconds)}</p>
          </div>
        )}

        {printer.accuracyPercent !== undefined && (
          <div>
            <p className="text-pf-text-secondary">Accuracy</p>
            <p className={`text-lg font-bold ${printer.accuracyPercent > 90 ? 'text-pf-success' : printer.accuracyPercent > 75 ? 'text-pf-warning' : 'text-pf-danger'}`}>
              {printer.accuracyPercent.toFixed(0)}%
            </p>
          </div>
        )}
      </div>

      {printer.minActualSeconds !== undefined && printer.maxActualSeconds !== undefined && (
        <div className="mt-3 pt-3 border-t border-pf-border">
          <p className="text-xs text-pf-text-secondary mb-2">Duration Range</p>
          <div className="flex gap-2">
            <div className="flex-1">
              <p className="text-xs text-pf-text-secondary">Shortest</p>
              <p className="font-semibold text-pf-text-primary">{formatDuration(printer.minActualSeconds)}</p>
            </div>
            <div className="flex-1">
              <p className="text-xs text-pf-text-secondary">Longest</p>
              <p className="font-semibold text-pf-text-primary">{formatDuration(printer.maxActualSeconds)}</p>
            </div>
          </div>
        </div>
      )}
    </div>
  );

  return (
    <div className="space-y-6">
      {/* Overall Metrics */}
      <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-6">
        <h3 className="text-lg font-semibold text-pf-text-primary mb-6">Overall Duration Analytics</h3>

        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
          {renderMetricCard('Total Jobs Analyzed', analytics.totalJobs)}
          {renderMetricCard(
            'Average Estimated Time',
            formatDuration(analytics.averageEstimatedSeconds),
            'Across all jobs'
          )}
          {renderMetricCard(
            'Average Actual Time',
            formatDuration(analytics.averageActualSeconds),
            'Across all jobs'
          )}
          {renderMetricCard(
            'Overall Accuracy',
            analytics.overallAccuracyPercent || 0,
            'Estimate vs actual',
            true
          )}
        </div>

        {analytics.overallVariancePercent !== undefined && (
          <div className="bg-pf-bg-2 border border-pf-border rounded p-4">
            <p className="text-sm text-pf-text-secondary mb-2">Average Variance</p>
            <p className={`text-3xl font-bold ${analytics.overallVariancePercent > 10 ? 'text-pf-warning' : 'text-pf-success'}`}>
              {analytics.overallVariancePercent > 0 ? '+' : ''}{analytics.overallVariancePercent.toFixed(1)}%
            </p>
            <p className="text-xs text-pf-text-secondary mt-2">
              {analytics.overallVariancePercent > 0 ? 'Jobs typically take longer than estimated' : 'Jobs typically finish ahead of schedule'}
            </p>
          </div>
        )}
      </div>

      {/* Per-Printer Metrics */}
      {Object.keys(analytics.byPrinter).length > 0 && (
        <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-6">
          <h3 className="text-lg font-semibold text-pf-text-primary mb-6">Per-Printer Breakdown</h3>

          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
            {Object.values(analytics.byPrinter).map((printer) => renderPrinterStats(printer))}
          </div>
        </div>
      )}

      {/* Top Performers */}
      {analytics.topPerformers.length > 0 && (
        <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-6">
          <h3 className="text-lg font-semibold text-pf-text-primary mb-4">Top Performers</h3>
          <p className="text-sm text-pf-text-secondary mb-4">Printers with the most accurate time estimates</p>

          <div className="space-y-3">
            {analytics.topPerformers.map((printer) => (
              <div key={printer.printerId} className="flex items-center justify-between bg-pf-bg-2 border border-pf-border rounded p-4">
                <div>
                  <p className="font-medium text-pf-text-primary">{printer.printerName}</p>
                  <p className="text-sm text-pf-text-secondary">{printer.totalJobs} jobs completed</p>
                </div>
                <div className="text-right">
                  <p className="text-2xl font-bold text-pf-success">{printer.accuracyPercent?.toFixed(0)}%</p>
                  <p className="text-xs text-pf-text-secondary">accuracy</p>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
