import { useQuery } from '@tanstack/react-query';
import { Card } from '@/common/components/ui';
import { apiClient } from '@/services/api';

export function MetricsSummaryWidgets() {
  const { data: metrics } = useQuery({
    queryKey: ['monitoring-metrics-summary'],
    queryFn: () => apiClient.getMonitoringMetricsSummary(),
    staleTime: 10_000,
    refetchInterval: 30_000,
  });

  const widgets = [
    { label: 'Request Rate', value: metrics?.requestsPerSecond ?? 0, unit: 'req/s', decimals: 1, color: 'text-blue-400' },
    { label: 'Error Rate', value: metrics?.errorRatePercent ?? 0, unit: '%', decimals: 1, color: metrics && metrics.errorRatePercent > 1 ? 'text-red-400' : 'text-green-400' },
    { label: 'P95 Latency', value: metrics?.p95LatencyMs ?? 0, unit: 'ms', decimals: 1, color: metrics && metrics.p95LatencyMs > 500 ? 'text-yellow-400' : 'text-green-400' },
    { label: 'Memory', value: metrics?.memoryUsageMb ?? 0, unit: 'MB', decimals: 1, color: 'text-purple-400' },
    { label: 'Active Printers', value: metrics?.activePrinters ?? 0, unit: '', decimals: 0, color: 'text-cyan-400' },
    { label: 'Slicer Jobs (24h)', value: metrics?.slicerJobsLast24h ?? 0, unit: '', decimals: 0, color: 'text-orange-400' },
  ];

  return (
    <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-3">
      {widgets.map(w => (
        <Card key={w.label}>
          <Card.Body className="p-3 text-center">
            <div className="text-xs text-pf-text-secondary mb-1">{w.label}</div>
            <div className={`text-xl font-semibold ${w.color}`}>
              {typeof w.value === 'number' ? w.value.toFixed(w.decimals) : w.value}
              {w.unit && <span className="text-xs ml-0.5 text-pf-text-secondary">{w.unit}</span>}
            </div>
          </Card.Body>
        </Card>
      ))}
    </div>
  );
}
