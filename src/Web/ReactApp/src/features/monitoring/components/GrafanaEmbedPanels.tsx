import { useState, memo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Card } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import type { MonitoringMetricsSummaryDto } from '@/types/api';

const DASHBOARD_UID = 'printfarmer-overview';

interface PanelConfig {
  id: number;
  title: string;
  /** DTO key whose value must be > 0 for the Grafana iframe to render */
  metricKey: keyof MonitoringMetricsSummaryDto;
  noDataMessage: string;
  noDataHint: string;
}

const PANELS: PanelConfig[] = [
  { id: 5, title: 'Request Rate Over Time', metricKey: 'requestsPerSecond', noDataMessage: 'No HTTP traffic detected right now.', noDataHint: 'This panel will populate automatically when the API receives requests.' },
  { id: 6, title: 'Response Latency Distribution', metricKey: 'requestsPerSecond', noDataMessage: 'No latency data available yet.', noDataHint: 'This panel will populate once the API starts handling requests.' },
  { id: 9, title: 'Error Rate Over Time', metricKey: 'errorRatePercent', noDataMessage: 'No HTTP errors detected — everything looks healthy.', noDataHint: 'This panel will show error trends if 4xx or 5xx responses occur.' },
  { id: 10, title: 'Memory Usage Over Time', metricKey: 'memoryUsageMb', noDataMessage: 'Memory metrics not available yet.', noDataHint: 'This panel will populate once .NET runtime metrics are being collected.' },
  { id: 7, title: 'Printer Operations', metricKey: 'activePrinters', noDataMessage: 'No active printers detected right now.', noDataHint: 'This panel will populate automatically when printer activity is available.' },
  { id: 8, title: 'Slicer Operations', metricKey: 'slicerJobsLast24h', noDataMessage: 'No slicer jobs detected in the last 24 hours.', noDataHint: 'This panel will populate automatically once slicing is active.' },
  { id: 11, title: 'Database Operations Over Time', metricKey: 'databaseOperationsLast24h', noDataMessage: 'No database operation metrics available.', noDataHint: 'This panel will populate when database activity is tracked.' },
  { id: 12, title: 'File Operations Over Time', metricKey: 'fileOperationsLast24h', noDataMessage: 'No file operation metrics available.', noDataHint: 'This panel will populate when file activity is tracked.' },
  { id: 13, title: 'Request Rate by Endpoint', metricKey: 'requestsPerSecond', noDataMessage: 'No per-endpoint traffic data available yet.', noDataHint: 'This panel will show top endpoints once HTTP traffic is detected.' },
  { id: 14, title: 'API Latency Percentiles Over Time', metricKey: 'p95LatencyMs', noDataMessage: 'No latency percentile data available yet.', noDataHint: 'This panel will show p50/p95/p99 latency once requests are processed.' },
];

export function GrafanaEmbedPanels() {
  // Read from the TanStack cache that useStreamingMetrics already populates.
  // No refetchInterval — the SSE hook handles periodic updates.
  // staleTime: Infinity prevents this query from overwriting SSE data.
  const { data: metrics } = useQuery({
    queryKey: ['monitoring-metrics-summary'],
    queryFn: () => apiClient.getMonitoringMetricsSummary(),
    staleTime: Infinity,
    refetchInterval: false,
  });

  return (
    <div>
      <h3 className="text-sm font-medium text-pf-text-secondary mb-3">Live Charts</h3>
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {PANELS.map(panel => {
          // Only show fallback when metrics have loaded and the relevant value is 0/missing.
          // While metrics are still loading (undefined), render the iframe — it may have data.
          const metricValue = metrics?.[panel.metricKey];
          const showFallback = metrics != null && (metricValue == null || metricValue === 0);

          if (showFallback) {
            return (
              <Card key={panel.id}>
                <Card.Body className="h-[250px] flex flex-col items-center justify-center text-center px-6">
                  <div className="text-pf-text-primary font-medium">{panel.title}</div>
                  <div className="mt-2 text-sm text-pf-text-secondary">{panel.noDataMessage}</div>
                  <div className="mt-1 text-xs text-pf-text-tertiary">{panel.noDataHint}</div>
                </Card.Body>
              </Card>
            );
          }

          return <GrafanaPanel key={panel.id} panelId={panel.id} title={panel.title} />;
        })}
      </div>
    </div>
  );
}

const GrafanaPanel = memo(function GrafanaPanel({ panelId, title }: { panelId: number; title: string }) {
  const [hasError, setHasError] = useState(false);
  const src = `/grafana/d-solo/${DASHBOARD_UID}/printfarmer-overview?panelId=${panelId}&refresh=30s&theme=dark`;

  const handleError = () => {
    console.warn(`[Monitoring] Failed to load Grafana panel ${panelId}: "${title}"`);
    setHasError(true);
  };

  if (hasError) {
    return (
      <Card>
        <Card.Body className="h-[250px] flex items-center justify-center text-pf-text-secondary text-sm">
          Unable to load "{title}" panel
        </Card.Body>
      </Card>
    );
  }

  return (
    <Card>
      <Card.Body className="p-0 overflow-hidden rounded-lg">
        <iframe
          src={src}
          title={title}
          className="w-full h-[250px] border-0"
          onError={handleError}
          loading="lazy"
        />
      </Card.Body>
    </Card>
  );
});
