import { useEffect, useRef, useCallback } from 'react';
import { useQuery, useMutation } from '@tanstack/react-query';
import { toast } from 'sonner';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Card, Spinner, Badge } from '@/common/components/ui';
import { ChartIcon, ExternalLinkIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { MetricsSummaryWidgets } from '@/features/monitoring/components/MetricsSummaryWidgets';
import { GrafanaEmbedPanels } from '@/features/monitoring/components/GrafanaEmbedPanels';
import type { MonitoringStatusDto } from '@/types/api';

const SESSION_REFRESH_INTERVAL_MS = 10 * 60 * 1000; // 10 min (cookie TTL is 15 min)

export function MonitoringPage() {
  const sessionRefreshRef = useRef<ReturnType<typeof setInterval>>();

  const { data: status, isLoading: statusLoading, error: statusError } = useQuery({
    queryKey: ['monitoring-status'],
    queryFn: () => apiClient.getMonitoringStatus(),
    staleTime: 30_000,
    refetchInterval: 30_000,
  });

  const sessionMutation = useMutation({
    mutationFn: () => apiClient.createMonitoringSession(),
    onError: (err: Error) => toast.error(`Failed to create monitoring session: ${err.message}`),
  });

  const refreshSession = useCallback(() => {
    sessionMutation.mutate();
  }, [sessionMutation]);

  // Create monitoring session on mount and auto-refresh
  useEffect(() => {
    refreshSession();
    sessionRefreshRef.current = setInterval(refreshSession, SESSION_REFRESH_INTERVAL_MS);
    return () => {
      if (sessionRefreshRef.current) clearInterval(sessionRefreshRef.current);
    };
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  if (statusLoading) {
    return (
      <PageTemplate title="Monitoring" icon={ChartIcon}>
        <div className="flex justify-center py-12"><Spinner size="lg" /></div>
      </PageTemplate>
    );
  }

  // Only show error view on initial load failure (no cached data).
  // Background refetch errors keep the last-known UI visible to avoid
  // unmounting all Grafana iframes (which causes the whole page to flash).
  if (statusError && !status) {
    return (
      <PageTemplate title="Monitoring" icon={ChartIcon}>
        <div className="p-4 text-pf-error">Failed to load monitoring status: {String(statusError)}</div>
      </PageTemplate>
    );
  }

  const anyAvailable = status?.grafana.available || status?.jaeger.available || status?.prometheus.available;

  return (
    <PageTemplate
      title="Monitoring"
      subtitle="Application metrics, traces, and observability"
      icon={ChartIcon}
      actions={<DeepLinks status={status} />}
    >
      {!anyAvailable ? (
        <Card>
          <Card.Body>
            <div className="text-center py-8 text-pf-text-secondary">
              <ChartIcon className="w-12 h-12 mx-auto mb-3 opacity-40" />
              <p className="text-lg font-medium mb-1">Monitoring Not Configured</p>
              <p className="text-sm">
                Deploy the monitoring stack (Prometheus, Grafana, Jaeger) to enable observability features.
              </p>
            </div>
          </Card.Body>
        </Card>
      ) : (
        <div className="space-y-6">
          <ServiceStatusBar status={status} />
          {status?.prometheus.available && <MetricsSummaryWidgets />}
          {status?.grafana.available && <GrafanaEmbedPanels />}
        </div>
      )}
    </PageTemplate>
  );
}

function ServiceStatusBar({ status }: { status?: MonitoringStatusDto }) {
  if (!status) return null;
  const services = [
    { name: 'Prometheus', available: status.prometheus.available },
    { name: 'Grafana', available: status.grafana.available },
    { name: 'Jaeger', available: status.jaeger.available },
  ];

  return (
    <div className="flex gap-2">
      {services.map(s => (
        <Badge key={s.name} variant={s.available ? 'success' : 'error'} size="sm">
          {s.name}: {s.available ? 'Online' : 'Offline'}
        </Badge>
      ))}
    </div>
  );
}

function DeepLinks({ status }: { status?: MonitoringStatusDto }) {
  return (
    <div className="flex gap-2">
      {status?.grafana.available && (
        <a
          href="/grafana/"
          target="_blank"
          rel="noopener noreferrer"
          className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-md border border-pf-border text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2 transition-colors"
        >
          Open Grafana
          <ExternalLinkIcon className="w-3.5 h-3.5" />
        </a>
      )}
      {status?.jaeger.available && (
        <a
          href="/jaeger/"
          target="_blank"
          rel="noopener noreferrer"
          className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-md border border-pf-border text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2 transition-colors"
        >
          Open Jaeger
          <ExternalLinkIcon className="w-3.5 h-3.5" />
        </a>
      )}
    </div>
  );
}
