import { useQuery } from '@tanstack/react-query';
import { Alert } from '@/common/components/ui/Alert';
import { Badge, Card, Spinner } from '@/common/components/ui';
import { apiClient } from '@/services/api';

function formatTimestamp(value?: string): string {
  if (!value) {
    return 'Waiting for first scan';
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  return parsed.toLocaleTimeString([], {
    hour: 'numeric',
    minute: '2-digit',
  });
}

function formatStateLabel(state: string): string {
  switch (state) {
    case 'monitoring':
      return 'Monitoring';
    case 'misconfigured':
      return 'Needs attention';
    case 'error':
      return 'Error';
    case 'idle':
      return 'Idle';
    default:
      return 'Disabled';
  }
}

function stateVariant(state: string): 'default' | 'success' | 'warning' | 'error' {
  switch (state) {
    case 'monitoring':
      return 'success';
    case 'misconfigured':
      return 'warning';
    case 'error':
      return 'error';
    default:
      return 'default';
  }
}

export function FailureDetectionStatusCard() {
  const { data, isLoading, error } = useQuery({
    queryKey: ['failure-detection-status'],
    queryFn: () => apiClient.getFailureDetectionStatus(),
    staleTime: 15_000,
    refetchInterval: 30_000,
  });

  if (isLoading) {
    return (
      <Card>
        <Card.Body className="flex justify-center py-6">
          <Spinner size="sm" />
        </Card.Body>
      </Card>
    );
  }

  if (error && !data) {
    return (
      <Alert type="warning" title="Failure detection status unavailable">
        Could not load the live spaghetti-detection runtime status.
      </Alert>
    );
  }

  if (!data) {
    return null;
  }

  const activePrinters = data.printers.filter(printer => printer.state === 'monitoring');
  const attentionPrinters = data.printers.filter(printer => printer.state === 'misconfigured' || printer.state === 'error').slice(0, 3);

  return (
    <Card>
      <Card.Header>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <div className="text-sm font-semibold text-pf-text-primary">Failure Detection Runtime</div>
            <div className="text-xs text-pf-text-secondary">
              Last scan {formatTimestamp(data.lastScanCompletedAt)}
            </div>
          </div>
          <div className="flex flex-wrap gap-2">
            <Badge variant={data.monitoringEnabled ? 'success' : 'default'} size="sm">
              Global {data.monitoringEnabled ? 'On' : 'Off'}
            </Badge>
            <Badge variant={data.autoPauseOnFailure ? 'error' : 'warning'} size="sm">
              Auto-pause {data.autoPauseOnFailure ? 'On' : 'Off'}
            </Badge>
          </div>
        </div>
      </Card.Header>
      <Card.Body className="space-y-4">
        <div className="grid gap-3 sm:grid-cols-4">
          <MetricTile label="Configured" value={String(data.configuredPrinterCount)} />
          <MetricTile label="Actively monitored" value={String(data.activelyMonitoredPrinterCount)} />
          <MetricTile label="Last analyzed" value={String(data.lastAnalyzedPrinterCount)} />
          <MetricTile label="Last failures" value={String(data.lastFailureCount)} />
        </div>

        {data.lastError && (
          <Alert type="error" title="Last monitoring error">
            {data.lastError}
          </Alert>
        )}

        {data.printers.length === 0 ? (
          <div className="rounded-lg border border-dashed border-pf-border px-4 py-3 text-sm text-pf-text-secondary">
            No printers have Obico monitoring enabled yet.
          </div>
        ) : (
          <div className="grid gap-4 lg:grid-cols-[1.2fr,1fr]">
            <div className="space-y-2">
              <div className="text-xs font-semibold uppercase tracking-wide text-pf-text-secondary">
                Printers needing attention
              </div>
              {attentionPrinters.length === 0 ? (
                <div className="rounded-lg border border-pf-border bg-pf-bg-1 px-4 py-3 text-sm text-pf-text-secondary">
                  No printers are currently blocked or erroring.
                </div>
              ) : (
                <div className="space-y-2">
                  {attentionPrinters.map(printer => (
                    <div
                      key={printer.printerId}
                      className="rounded-lg border border-pf-border bg-pf-bg-1 px-4 py-3"
                    >
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        <div className="font-medium text-pf-text-primary">{printer.printerName}</div>
                        <Badge variant={stateVariant(printer.state)} size="sm">
                          {formatStateLabel(printer.state)}
                        </Badge>
                      </div>
                      <div className="mt-1 text-sm text-pf-text-secondary">{printer.reason}</div>
                    </div>
                  ))}
                </div>
              )}
            </div>

            <div className="space-y-2">
              <div className="text-xs font-semibold uppercase tracking-wide text-pf-text-secondary">
                Active now
              </div>
              {activePrinters.length === 0 ? (
                <div className="rounded-lg border border-pf-border bg-pf-bg-1 px-4 py-3 text-sm text-pf-text-secondary">
                  No printers are actively being monitored right now.
                </div>
              ) : (
                <div className="flex flex-wrap gap-2">
                  {activePrinters.map(printer => (
                    <Badge key={printer.printerId} variant="success" size="sm">
                      {printer.printerName}
                    </Badge>
                  ))}
                </div>
              )}
            </div>
          </div>
        )}
      </Card.Body>
    </Card>
  );
}

function MetricTile({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-pf-border bg-pf-bg-1 px-4 py-3">
      <div className="text-xs font-semibold uppercase tracking-wide text-pf-text-secondary">{label}</div>
      <div className="mt-1 text-xl font-semibold text-pf-text-primary">{value}</div>
    </div>
  );
}
