import { Card } from '@/common/components/ui';
import { useStreamingMetrics } from '@/features/monitoring/hooks/useStreamingMetrics';

export function MetricsSummaryWidgets() {
  const { metrics, loadedKeys } = useStreamingMetrics();

  const widgets = [
    { key: 'requestsPerSecond', label: 'Request Rate', value: metrics?.requestsPerSecond ?? 0, unit: 'req/s', decimals: 1, color: 'text-blue-400' },
    { key: 'errorRatePercent', label: 'Error Rate', value: metrics?.errorRatePercent ?? 0, unit: '%', decimals: 1, color: metrics?.errorRatePercent !== undefined && metrics.errorRatePercent > 1 ? 'text-red-400' : 'text-green-400' },
    { key: 'p95LatencyMs', label: 'P95 Latency', value: metrics?.p95LatencyMs ?? 0, unit: 'ms', decimals: 1, color: metrics?.p95LatencyMs !== undefined && metrics.p95LatencyMs > 500 ? 'text-yellow-400' : 'text-green-400' },
    { key: 'memoryUsageMb', label: 'Memory', value: metrics?.memoryUsageMb ?? 0, unit: 'MB', decimals: 1, color: 'text-purple-400' },
    { key: 'activePrinters', label: 'Active Printers', value: metrics?.activePrinters ?? 0, unit: '', decimals: 0, color: 'text-cyan-400' },
    { key: 'apiCallsLast24h', label: 'API Calls (24h)', value: metrics?.apiCallsLast24h ?? 0, unit: '', decimals: 0, color: 'text-sky-400' },
  ];

  return (
    <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-3">
      {widgets.map(w => {
        const loaded = loadedKeys.has(w.key);
        return (
          <Card key={w.label}>
            <Card.Body className="p-3 text-center">
              <div className="text-xs text-pf-text-secondary mb-1">{w.label}</div>
              <div className={`text-xl font-semibold ${loaded ? w.color : 'text-pf-text-tertiary'}`}>
                {loaded
                  ? (
                    <>
                      {typeof w.value === 'number' ? w.value.toFixed(w.decimals) : w.value}
                      {w.unit && <span className="text-xs ml-0.5 text-pf-text-secondary">{w.unit}</span>}
                    </>
                  )
                  : <span className="inline-block w-12 h-6 bg-pf-surface-secondary rounded animate-pulse" />
                }
              </div>
            </Card.Body>
          </Card>
        );
      })}
    </div>
  );
}
