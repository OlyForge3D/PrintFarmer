import { useState, useRef, useEffect, memo, useCallback } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Card, Checkbox, Button } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import { useSlicer } from '@/hooks/useSlicer';
import type { MonitoringMetricsSummaryDto } from '@/types/api';

const DASHBOARD_UID = 'printfarmer-overview';

const SLICER_PANEL_ID = 8;

interface PanelConfig {
  id: number;
  title: string;
  /** DTO key whose value must be > 0 for the Grafana iframe to render */
  metricKey: keyof MonitoringMetricsSummaryDto;
}

const PANELS: PanelConfig[] = [
  { id: 5, title: 'Request Rate Over Time', metricKey: 'requestsPerSecond' },
  { id: 6, title: 'Response Latency Distribution', metricKey: 'requestsPerSecond' },
  { id: 9, title: 'Error Rate Over Time', metricKey: 'errorRatePercent' },
  { id: 10, title: 'Memory Usage Over Time', metricKey: 'memoryUsageMb' },
  { id: 7, title: 'Printer Operations', metricKey: 'activePrinters' },
  { id: 8, title: 'Slicer Operations', metricKey: 'slicerJobsLast24h' },
  { id: 11, title: 'Database Operations Over Time', metricKey: 'databaseOperationsLast24h' },
  { id: 12, title: 'File Operations Over Time', metricKey: 'fileOperationsLast24h' },
  { id: 13, title: 'Request Rate by Endpoint', metricKey: 'requestsPerSecond' },
  { id: 14, title: 'API Latency Percentiles Over Time', metricKey: 'p95LatencyMs' },
  { id: 15, title: 'Slowest Endpoints (P95)', metricKey: 'requestsPerSecond' },
  { id: 16, title: 'FD: Analysis Duration Over Time', metricKey: 'activePrinters' },
  { id: 17, title: 'FD: Analyses & Failures Rate', metricKey: 'activePrinters' },
  { id: 18, title: 'FD: ML Confidence Over Time', metricKey: 'activePrinters' },
  { id: 19, title: 'FD: Cycle Duration Over Time', metricKey: 'activePrinters' },
  { id: 20, title: 'FD: Active vs Configured Printers', metricKey: 'activePrinters' },
  { id: 21, title: 'FD: Auto-Pauses Triggered', metricKey: 'activePrinters' },
];

const PANEL_VISIBILITY_KEY = 'monitoring-grafana-panels';

function loadVisiblePanelIds(): Set<number> {
  try {
    const raw = localStorage.getItem(PANEL_VISIBILITY_KEY);
    if (raw) {
      const ids = JSON.parse(raw) as number[];
      if (Array.isArray(ids)) return new Set(ids);
    }
  } catch { /* noop */ }
  // Default: all panels visible
  return new Set(PANELS.map(p => p.id));
}

function saveVisiblePanelIds(ids: Set<number>) {
  try { localStorage.setItem(PANEL_VISIBILITY_KEY, JSON.stringify([...ids])); } catch { /* noop */ }
}

function PanelSelector({ visibleIds, onToggle, onSelectAll, onSelectNone }: {
  visibleIds: Set<number>;
  onToggle: (id: number) => void;
  onSelectAll: () => void;
  onSelectNone: () => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const handleClick = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [open]);

  return (
    <div className="relative" ref={ref}>
      <Button variant="secondary" size="sm" onClick={() => setOpen(o => !o)}>
        Charts ({visibleIds.size}/{PANELS.length})
      </Button>
      {open && (
        <div className="absolute right-0 top-full mt-1 z-50 w-72 rounded-lg border border-pf-border bg-pf-bg-0 shadow-lg">
          <div className="flex items-center justify-between border-b border-pf-border px-3 py-2">
            <span className="text-xs font-medium text-pf-text-secondary">Visible Charts</span>
            <div className="flex gap-2">
              <Button variant="ghost" size="sm" onClick={onSelectAll} className="text-xs! px-1! py-0!">All</Button>
              <Button variant="ghost" size="sm" onClick={onSelectNone} className="text-xs! px-1! py-0!">None</Button>
            </div>
          </div>
          <div className="max-h-72 overflow-y-auto py-1">
            {PANELS.map(panel => (
              <label
                key={panel.id}
                className="flex items-center gap-2 px-3 py-1.5 cursor-pointer hover:bg-pf-bg-1 transition-colors"
              >
                <Checkbox
                  checked={visibleIds.has(panel.id)}
                  onChange={() => onToggle(panel.id)}
                />
                <span className="text-xs text-pf-text-primary truncate">{panel.title}</span>
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

export function GrafanaEmbedPanels({ sessionKey = 0 }: { sessionKey?: number }) {
  // Read from the TanStack cache that useStreamingMetrics already populates.
  // No refetchInterval — the SSE hook handles periodic updates.
  // staleTime: Infinity prevents this query from overwriting SSE data.
  const { data: metrics } = useQuery({
    queryKey: ['monitoring-metrics-summary'],
    queryFn: () => apiClient.getMonitoringMetricsSummary(),
    staleTime: Infinity,
    refetchInterval: false,
  });

  const { isSlicerAvailable } = useSlicer();
  const [visibleIds, setVisibleIds] = useState<Set<number>>(loadVisiblePanelIds);

  const togglePanel = useCallback((id: number) => {
    setVisibleIds(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      saveVisiblePanelIds(next);
      return next;
    });
  }, []);

  const selectAll = useCallback(() => {
    const all = new Set(PANELS.map(p => p.id));
    setVisibleIds(all);
    saveVisiblePanelIds(all);
  }, []);

  const selectNone = useCallback(() => {
    const none = new Set<number>();
    setVisibleIds(none);
    saveVisiblePanelIds(none);
  }, []);

  // Filter out the Slicer Operations panel entirely when slicing is disabled
  const candidatePanels = isSlicerAvailable ? PANELS : PANELS.filter(p => p.id !== SLICER_PANEL_ID);
  const visiblePanels = candidatePanels.filter(p => visibleIds.has(p.id));

  return (
    <div>
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-sm font-medium text-pf-text-secondary">Live Charts</h3>
        <PanelSelector
          visibleIds={visibleIds}
          onToggle={togglePanel}
          onSelectAll={selectAll}
          onSelectNone={selectNone}
        />
      </div>
      {visiblePanels.length === 0 ? (
        <Card>
          <Card.Body className="py-8 text-center text-pf-text-secondary text-sm">
            No charts selected. Use the Charts button above to pick which graphs to display.
          </Card.Body>
        </Card>
      ) : (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          {visiblePanels.map(panel => {
            const metricValue = metrics?.[panel.metricKey];
            const showFallback = metrics != null && (metricValue == null || metricValue === 0);

            if (showFallback) {
              return (
                <Card key={panel.id}>
                  <Card.Body className="h-62.5 flex flex-col items-center justify-center text-center px-6">
                    <div className="text-pf-text-primary font-medium">{panel.title}</div>
                    <div className="mt-2 text-sm text-pf-text-secondary">No data available yet</div>
                  </Card.Body>
                </Card>
              );
            }

            return <GrafanaPanel key={panel.id} panelId={panel.id} title={panel.title} sessionKey={sessionKey} />;
          })}
        </div>
      )}
    </div>
  );
}

const GrafanaPanel = memo(function GrafanaPanel({ panelId, title, sessionKey }: { panelId: number; title: string; sessionKey: number }) {
  const [hasError, setHasError] = useState(false);
  const src = `/grafana/d-solo/${DASHBOARD_UID}/printfarmer-overview?panelId=${panelId}&refresh=30s&theme=dark&_sk=${sessionKey}`;

  const handleError = () => {
    console.warn(`[Monitoring] Failed to load Grafana panel ${panelId}: "${title}"`);
    setHasError(true);
  };

  if (hasError) {
    return (
      <Card>
        <Card.Body className="h-62.5 flex items-center justify-center text-pf-text-secondary text-sm">
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
          className="w-full h-62.5 border-0"
          onError={handleError}
          loading="lazy"
        />
      </Card.Body>
    </Card>
  );
});
