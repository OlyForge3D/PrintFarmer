/**
 * MonitoringContent Component
 *
 * Extracted from MonitoringPage for embedding in SystemDashboardPage tabs.
 * Shows Prometheus metrics, Grafana panels, and service status — with
 * graceful degradation when optional services are unavailable.
 */

import { useEffect, useRef, useCallback, useState } from 'react';
import { useQuery, useMutation } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Card, Spinner, Badge, Button } from '@/common/components/ui';
import { ChartIcon, ExternalLinkIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { MetricsSummaryWidgets } from '@/features/monitoring/components/MetricsSummaryWidgets';
import { GrafanaEmbedPanels } from '@/features/monitoring/components/GrafanaEmbedPanels';
import type { MonitoringStatusDto } from '@/types/api';

const SESSION_REFRESH_INTERVAL_MS = 10 * 60 * 1000;

const ADVANCED_PREFS_KEY = 'monitoring-advanced-visible';

export function MonitoringContent() {
  const sessionRefreshRef = useRef<ReturnType<typeof setInterval>>(undefined);
  const [sessionKey, setSessionKey] = useState(0);
  const [showAdvanced, setShowAdvanced] = useState(() => {
    try { return localStorage.getItem(ADVANCED_PREFS_KEY) === 'true'; } catch { return false; }
  });

  const { data: status, isLoading, error } = useQuery({
    queryKey: ['monitoring-status'],
    queryFn: () => apiClient.getMonitoringStatus(),
    staleTime: 30_000,
    refetchInterval: 30_000,
  });

  const sessionMutation = useMutation({
    mutationFn: () => apiClient.createMonitoringSession(),
    retry: 3,
    retryDelay: 5_000,
    onSuccess: () => setSessionKey(k => k + 1),
    onError: (err: Error) => toast.error(`Failed to create monitoring session: ${err.message}`),
  });

  const refreshSession = useCallback(() => {
    sessionMutation.mutate();
  }, [sessionMutation]);

  useEffect(() => {
    refreshSession();
    sessionRefreshRef.current = setInterval(refreshSession, SESSION_REFRESH_INTERVAL_MS);
    return () => {
      if (sessionRefreshRef.current) clearInterval(sessionRefreshRef.current);
    };
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const toggleAdvanced = () => {
    const next = !showAdvanced;
    setShowAdvanced(next);
    try { localStorage.setItem(ADVANCED_PREFS_KEY, String(next)); } catch { /* noop */ }
  };

  if (isLoading) {
    return <div className="flex justify-center py-12"><Spinner size="lg" /></div>;
  }

  if (error && !status) {
    return <div className="p-4 text-pf-error">Failed to load monitoring status: {String(error)}</div>;
  }

  const anyAvailable = status?.grafana.available || status?.jaeger.available || status?.prometheus.available;

  if (!anyAvailable) {
    return (
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
    );
  }

  return (
    <div className="space-y-6">
      {/* Service status + deep links */}
      <div className="flex items-center justify-between flex-wrap gap-2">
        <ServiceStatusBar status={status} />
        <div className="flex gap-2 items-center">
          <Button
            variant={showAdvanced ? 'primary' : 'secondary'}
            size="sm"
            onClick={toggleAdvanced}
          >
            {showAdvanced ? 'Hide Advanced' : 'Show Advanced'}
          </Button>
          <DeepLinks status={status} />
        </div>
      </div>

      {/* Essential metrics — always shown when Prometheus available */}
      {status?.prometheus.available && <MetricsSummaryWidgets />}

      {/* Advanced: Grafana panels — opt-in */}
      {showAdvanced && status?.grafana.available && (
        <GrafanaEmbedPanels sessionKey={sessionKey} />
      )}

      {/* Info when Grafana not available but advanced requested */}
      {showAdvanced && !status?.grafana.available && (
        <Card>
          <Card.Body>
            <p className="text-sm text-pf-text-secondary">
              Grafana is not configured. Deploy the monitoring stack to enable detailed charts and dashboards.
            </p>
          </Card.Body>
        </Card>
      )}
    </div>
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
        <Badge key={s.name} variant={s.available ? 'success' : 'default'} size="sm">
          {s.name}: {s.available ? 'Online' : 'Not configured'}
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
          Grafana
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
          Jaeger
          <ExternalLinkIcon className="w-3.5 h-3.5" />
        </a>
      )}
    </div>
  );
}
