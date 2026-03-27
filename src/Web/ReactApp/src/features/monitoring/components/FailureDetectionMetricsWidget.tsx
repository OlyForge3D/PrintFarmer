/**
 * FailureDetectionMetricsWidget
 *
 * Shows live capacity-planning metrics from the failure-detection monitor.
 * Data is sourced from the in-memory status snapshot (GET /api/failure-detection/status),
 * not Prometheus, so it works without the monitoring stack deployed.
 */

import { useQuery } from '@tanstack/react-query';
import { Card, Spinner, Badge } from '@/common/components/ui';
import { ShieldIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import type { FailureDetectionMonitorStatusDto } from '@/types/api';

function formatDuration(startIso?: string, endIso?: string): string | null {
  if (!startIso || !endIso) return null;
  const ms = new Date(endIso).getTime() - new Date(startIso).getTime();
  if (Number.isNaN(ms) || ms < 0) return null;
  return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(1)}s`;
}

function formatTime(iso?: string): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', second: '2-digit' });
}

interface MetricCardProps {
  label: string;
  value: string | number;
  unit?: string;
  color?: string;
}

function MetricCard({ label, value, unit, color = 'text-pf-accent' }: MetricCardProps) {
  return (
    <Card>
      <Card.Body className="p-3 text-center">
        <div className="text-xs text-pf-text-secondary mb-1">{label}</div>
        <div className={`text-xl font-semibold ${color}`}>
          {value}
          {unit && <span className="text-xs ml-0.5 text-pf-text-secondary">{unit}</span>}
        </div>
      </Card.Body>
    </Card>
  );
}

export function FailureDetectionMetricsWidget() {
  const { data: status, isLoading, error } = useQuery<FailureDetectionMonitorStatusDto>({
    queryKey: ['failure-detection-status'],
    queryFn: () => apiClient.getFailureDetectionStatus(),
    refetchInterval: 15_000,
  });

  if (isLoading) {
    return (
      <Card>
        <Card.Body className="py-8">
          <div className="flex justify-center"><Spinner size="md" /></div>
        </Card.Body>
      </Card>
    );
  }

  if (error || !status) {
    return (
      <Card>
        <Card.Body className="py-6 text-center text-pf-text-secondary">
          <ShieldIcon className="w-8 h-8 mx-auto mb-2 opacity-40" />
          <p className="text-sm">Failure detection status unavailable</p>
        </Card.Body>
      </Card>
    );
  }

  const cycleDuration = formatDuration(status.lastScanStartedAt, status.lastScanCompletedAt);
  const lastScanTime = formatTime(status.lastScanCompletedAt);
  const scanInterval = status.scanIntervalSeconds;
  const activeCount = status.activelyMonitoredPrinterCount;

  // Capacity estimate: if cycle takes > 80% of scan interval, flag it
  let cycleDurationMs = 0;
  if (status.lastScanStartedAt && status.lastScanCompletedAt) {
    cycleDurationMs = new Date(status.lastScanCompletedAt).getTime() - new Date(status.lastScanStartedAt).getTime();
  }
  const capacityPercent = scanInterval > 0 ? Math.round((cycleDurationMs / (scanInterval * 1000)) * 100) : 0;
  const capacityColor = capacityPercent > 80 ? 'text-pf-error' : capacityPercent > 50 ? 'text-pf-warning' : 'text-pf-success';

  // Aggregate confidence from per-printer statuses
  const monitoringPrinters = status.printers.filter(p => p.state === 'monitoring' && p.lastConfidence != null);
  const avgConfidence = monitoringPrinters.length > 0
    ? monitoringPrinters.reduce((sum, p) => sum + (p.lastConfidence ?? 0), 0) / monitoringPrinters.length
    : null;
  const avgPrintHealth = avgConfidence != null ? Math.round((1 - avgConfidence) * 100) : null;

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <ShieldIcon className="w-4 h-4 text-pf-success" ariaLabel="Failure detection metrics" />
          <h3 className="text-sm font-semibold text-pf-text-primary">Failure Detection</h3>
        </div>
        <Badge variant={status.monitoringEnabled ? 'success' : 'default'} size="sm">
          {status.monitoringEnabled ? 'Active' : 'Disabled'}
        </Badge>
      </div>

      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-3">
        <MetricCard
          label="Configured"
          value={status.configuredPrinterCount}
          color="text-pf-accent"
        />
        <MetricCard
          label="Actively Monitored"
          value={activeCount}
          color="text-pf-accent"
        />
        <MetricCard
          label="Cycle Duration"
          value={cycleDuration ?? '—'}
          color={capacityColor}
        />
        <MetricCard
          label="Cycle Capacity"
          value={capacityPercent}
          unit="%"
          color={capacityColor}
        />
        <MetricCard
          label="Avg Print Health"
          value={avgPrintHealth != null ? `${avgPrintHealth}` : '—'}
          unit={avgPrintHealth != null ? '%' : undefined}
          color={avgPrintHealth != null && avgPrintHealth < 50 ? 'text-pf-error' : 'text-pf-success'}
        />
        <MetricCard
          label="Failures Detected"
          value={status.lastFailureCount}
          color={status.lastFailureCount > 0 ? 'text-pf-error' : 'text-pf-success'}
        />
      </div>

      {lastScanTime && (
        <p className="text-xs text-pf-text-tertiary text-right">
          Last scan {lastScanTime} · Interval {scanInterval}s
          {status.lastError && <span className="text-pf-error ml-2">· Error: {status.lastError}</span>}
        </p>
      )}

      {capacityPercent > 80 && (
        <div className="rounded border border-pf-warning/30 bg-pf-warning-bg/40 px-3 py-2 text-sm text-pf-text-primary">
          <span className="font-medium text-pf-warning-text">Capacity warning:</span>{' '}
          Cycle takes {capacityPercent}% of the scan interval.
          {activeCount > 1 && ' Consider adding another Obico server to distribute the load.'}
        </div>
      )}
    </div>
  );
}
