import { DurationAnalyticsDto } from '@/services/printQueueService';

interface CompletionPredictionProps {
  analytics: DurationAnalyticsDto;
}

export function CompletionPrediction({ analytics }: CompletionPredictionProps) {
  const getPredictionColor = (variance?: number) => {
    if (variance === undefined) return 'pf-border';
    if (Math.abs(variance) < 5) return 'pf-success'; // Very accurate
    if (Math.abs(variance) < 15) return 'pf-info'; // Reasonably accurate
    if (Math.abs(variance) < 30) return 'pf-warning'; // Less accurate
    return 'pf-danger'; // Very inaccurate
  };

  const getPredictionMessage = (variance?: number) => {
    if (variance === undefined) return 'Unable to determine prediction accuracy';
    if (Math.abs(variance) < 5) return 'Very reliable estimates - jobs finish close to expected time';
    if (Math.abs(variance) < 15) return 'Good estimate accuracy - expect minor variations';
    if (Math.abs(variance) < 30) return 'Moderate estimate accuracy - build in safety margin';
    return 'Low estimate accuracy - significant variance expected';
  };

  const getDirectionMessage = (variance?: number) => {
    if (variance === undefined) return 'Check historical data';
    if (variance > 2) return 'Jobs typically run longer than estimated';
    if (variance < -2) return 'Jobs typically finish ahead of schedule';
    return 'Jobs typically match estimates';
  };

  const calculateEstimatedCompletion = (estimatedSeconds?: number) => {
    if (!estimatedSeconds) return null;
    
    const now = new Date();
    const completionMs = now.getTime() + (estimatedSeconds * 1000);
    const completionTime = new Date(completionMs);

    // Apply variance correction if available
    if (analytics.overallVariancePercent !== undefined) {
      const correctedMs = completionMs + (estimatedSeconds * 1000 * (analytics.overallVariancePercent / 100));
      return new Date(correctedMs);
    }

    return completionTime;
  };

  const formatTime = (date: Date) => {
    return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  };

  const formatDate = (date: Date) => {
    return date.toLocaleDateString([], { month: 'short', day: 'numeric', year: '2-digit' });
  };

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-6 space-y-6">
      <h3 className="text-lg font-semibold text-pf-text-primary">Completion Time Predictions</h3>

      {/* Prediction Reliability */}
      <div className={`border-l-4 border-pf-${getPredictionColor(analytics.overallVariancePercent)} bg-pf-bg-2 rounded p-4`}>
        <h4 className="font-semibold text-pf-text-primary mb-2">Prediction Reliability</h4>
        <p className={`text-sm text-pf-${getPredictionColor(analytics.overallVariancePercent)} font-medium`}>
          {getPredictionMessage(analytics.overallVariancePercent)}
        </p>
        <p className="text-sm text-pf-text-secondary mt-2">
          {getDirectionMessage(analytics.overallVariancePercent)}
        </p>
      </div>

      {/* Variance Insights */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div className="bg-pf-bg-2 border border-pf-border rounded p-4">
          <p className="text-sm text-pf-text-secondary mb-2">Average Variance</p>
          <p className={`text-2xl font-bold ${analytics.overallVariancePercent && analytics.overallVariancePercent > 0 ? 'text-pf-warning' : 'text-pf-success'}`}>
            {analytics.overallVariancePercent !== undefined ? `${analytics.overallVariancePercent > 0 ? '+' : ''}${analytics.overallVariancePercent.toFixed(1)}%` : 'N/A'}
          </p>
          <p className="text-xs text-pf-text-secondary mt-2">
            vs estimated time
          </p>
        </div>

        <div className="bg-pf-bg-2 border border-pf-border rounded p-4">
          <p className="text-sm text-pf-text-secondary mb-2">Estimate Accuracy</p>
          <p className={`text-2xl font-bold ${analytics.overallAccuracyPercent && analytics.overallAccuracyPercent > 85 ? 'text-pf-success' : analytics.overallAccuracyPercent && analytics.overallAccuracyPercent > 70 ? 'text-pf-warning' : 'text-pf-danger'}`}>
            {analytics.overallAccuracyPercent !== undefined ? `${analytics.overallAccuracyPercent.toFixed(0)}%` : 'N/A'}
          </p>
          <p className="text-xs text-pf-text-secondary mt-2">
            based on past jobs
          </p>
        </div>
      </div>

      {/* Prediction Recommendations */}
      <div className="bg-pf-info bg-opacity-10 border border-pf-info border-opacity-30 rounded p-4">
        <h4 className="font-semibold text-pf-text-primary mb-3 flex items-center gap-2">
          <span className="text-lg">💡</span> Prediction Tips
        </h4>
        <ul className="space-y-2 text-sm text-pf-text-secondary">
          {analytics.overallVariancePercent && analytics.overallVariancePercent > 10 && (
            <li className="flex gap-2">
              <span>•</span>
              <span>Add ~{analytics.overallVariancePercent.toFixed(0)}% buffer to estimated times for safety margin</span>
            </li>
          )}
          {analytics.overallVariancePercent && analytics.overallVariancePercent < -5 && (
            <li className="flex gap-2">
              <span>•</span>
              <span>Jobs frequently finish ahead of schedule - monitor printers closely near estimated end time</span>
            </li>
          )}
          {analytics.needsAttention && analytics.needsAttention.length > 0 && (
            <li className="flex gap-2">
              <span>•</span>
              <span>{analytics.needsAttention.length} printer(s) show low estimate accuracy - review their settings</span>
            </li>
          )}
          <li className="flex gap-2">
            <span>•</span>
            <span>Accuracy improves with consistent printer maintenance and calibration</span>
          </li>
        </ul>
      </div>

      {/* Per-Printer Predictions */}
      {Object.values(analytics.byPrinter).length > 0 && (
        <div>
          <h4 className="font-semibold text-pf-text-primary mb-4">Per-Printer Prediction Factors</h4>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            {Object.values(analytics.byPrinter)
              .sort((a, b) => (b.accuracyPercent || 0) - (a.accuracyPercent || 0))
              .map((printer) => (
                <div key={printer.printerId} className="bg-pf-bg-2 border border-pf-border rounded p-3">
                  <p className="font-medium text-pf-text-primary">{printer.printerName}</p>
                  <p className="text-xs text-pf-text-secondary mb-2">{printer.totalJobs} past jobs</p>

                  <div className="flex items-center gap-2">
                    <div className="flex-1 bg-pf-border rounded-full h-2 overflow-hidden">
                      <div
                        className={`h-full bg-pf-${printer.accuracyPercent && printer.accuracyPercent > 85 ? 'success' : printer.accuracyPercent && printer.accuracyPercent > 70 ? 'warning' : 'danger'}`}
                        style={{ width: `${Math.min((printer.accuracyPercent || 0), 100)}%` }}
                      ></div>
                    </div>
                    <span className="text-xs font-medium text-pf-text-primary w-12 text-right">
                      {printer.accuracyPercent?.toFixed(0)}%
                    </span>
                  </div>

                  {printer.variancePercent !== undefined && (
                    <p className="text-xs text-pf-text-secondary mt-2">
                      Avg variance: <span className={printer.variancePercent > 0 ? 'text-pf-warning' : 'text-pf-success'}>
                        {printer.variancePercent > 0 ? '+' : ''}{printer.variancePercent.toFixed(1)}%
                      </span>
                    </p>
                  )}
                </div>
              ))}
          </div>
        </div>
      )}
    </div>
  );
}
