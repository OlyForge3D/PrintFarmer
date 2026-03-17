import { PageTemplate } from '@/common/components/PageTemplate';
import { Card, Spinner, Badge, Button, Toggle } from '@/common/components/ui';
import { PlayIcon, CheckIcon, SkipForwardIcon, StopIcon } from '@/common/components/icons/MdiIcons';
import {
  useAutoPrintStatus,
  useMarkPrinterReady,
  useSkipAutoPrintJob,
  useCancelAutoPrint,
  useSetAutoPrintEnabled,
  useSetAutoPrintGlobalEnabled,
} from '@/common/hooks/useApi';
import type { AutoPrintStatus } from '@/types/api';
import clsx from 'clsx';

export function AutoPrintDashboardPage() {
  const { data: status, isLoading, error } = useAutoPrintStatus();
  const markReadyMutation = useMarkPrinterReady();
  const skipMutation = useSkipAutoPrintJob();
  const cancelMutation = useCancelAutoPrint();
  const setEnabledMutation = useSetAutoPrintEnabled();
  const setGlobalEnabledMutation = useSetAutoPrintGlobalEnabled();

  const handleGlobalToggle = (enabled: boolean) => {
    setGlobalEnabledMutation.mutate(enabled);
  };

  const handlePrinterToggle = (printerId: string, enabled: boolean) => {
    setEnabledMutation.mutate({ printerId, enabled });
  };

  const handleMarkReady = (printerId: string) => {
    markReadyMutation.mutate(printerId);
  };

  const handleSkip = (printerId: string) => {
    skipMutation.mutate(printerId);
  };

  const handleCancel = (printerId: string) => {
    cancelMutation.mutate(printerId);
  };

  if (isLoading) {
    return (
      <PageTemplate title="Auto-Print Dashboard" icon={PlayIcon}>
        <div className="flex justify-center py-12"><Spinner size="lg" /></div>
      </PageTemplate>
    );
  }

  if (error) {
    return (
      <PageTemplate title="Auto-Print Dashboard" icon={PlayIcon}>
        <div className="p-4 text-pf-error">Failed to load auto-print status: {error instanceof Error ? error.message : String(error)}</div>
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="Auto-Print Dashboard"
      subtitle="Smart ready-gate status and queue automation"
      icon={PlayIcon}
      actions={
        <div className="flex items-center gap-3">
          <span className="text-sm text-pf-text-secondary">Global Auto-Print:</span>
          <Toggle
            checked={status?.globalEnabled ?? false}
            onChange={handleGlobalToggle}
            disabled={setGlobalEnabledMutation.isPending}
            aria-label="Global auto-print toggle"
          />
        </div>
      }
    >
      {!status?.printers || status.printers.length === 0 ? (
        <Card>
          <Card.Body>
            <div className="text-center py-8 text-pf-text-secondary">
              <PlayIcon className="w-12 h-12 mx-auto mb-3 opacity-40" />
              <p className="text-lg font-medium mb-1">No Printers Configured</p>
              <p className="text-sm">Configure printers to enable auto-print queue management.</p>
            </div>
          </Card.Body>
        </Card>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
          {status.printers.map((printer) => (
            <PrinterStatusCard
              key={printer.printerId}
              printer={printer}
              onToggle={handlePrinterToggle}
              onMarkReady={handleMarkReady}
              onSkip={handleSkip}
              onCancel={handleCancel}
              isPending={
                markReadyMutation.isPending ||
                skipMutation.isPending ||
                cancelMutation.isPending ||
                setEnabledMutation.isPending
              }
            />
          ))}
        </div>
      )}
    </PageTemplate>
  );
}

interface PrinterStatusCardProps {
  printer: AutoPrintStatus;
  onToggle: (printerId: string, enabled: boolean) => void;
  onMarkReady: (printerId: string) => void;
  onSkip: (printerId: string) => void;
  onCancel: (printerId: string) => void;
  isPending: boolean;
}

function PrinterStatusCard({
  printer,
  onToggle,
  onMarkReady,
  onSkip,
  onCancel,
  isPending,
}: PrinterStatusCardProps) {
  const getStatusBadge = () => {
    if (!printer.enabled) {
      return <Badge variant="default" size="sm">Disabled</Badge>;
    }
    if (printer.isReady) {
      return <Badge variant="success" size="sm">Ready</Badge>;
    }
    return <Badge variant="warning" size="sm">Not Ready</Badge>;
  };

  return (
    <Card>
      <Card.Header className="flex items-start justify-between">
        <div className="flex-1 min-w-0">
          <h3 className="text-lg font-semibold text-pf-text-primary truncate">{printer.printerName}</h3>
          <div className="flex items-center gap-2 mt-1">
            {getStatusBadge()}
            <span className="text-xs text-pf-text-tertiary">
              Queue: {printer.queueDepth} {printer.queueDepth === 1 ? 'job' : 'jobs'}
            </span>
          </div>
        </div>
        <Toggle
          checked={printer.enabled}
          onChange={(enabled) => onToggle(printer.printerId, enabled)}
          disabled={isPending}
          aria-label={`Toggle auto-print for ${printer.printerName}`}
        />
      </Card.Header>
      <Card.Body>
        {printer.currentJobName && (
          <div className="mb-4 p-3 bg-pf-bg-1 rounded border border-pf-border">
            <div className="text-xs text-pf-text-tertiary mb-1">Current Job</div>
            <div className="text-sm text-pf-text-primary font-medium truncate">{printer.currentJobName}</div>
          </div>
        )}

        <div className="space-y-2 mb-4">
          <div className="text-xs font-semibold text-pf-text-secondary uppercase tracking-wider mb-2">
            Ready-Gate Checks
          </div>
          {printer.readyGateChecks.map((check, idx) => (
            <div key={idx} className="flex items-start gap-2">
              <div className={clsx('mt-0.5 flex-shrink-0', check.passed ? 'text-pf-success' : 'text-pf-error')}>
                {check.passed ? (
                  <CheckIcon className="w-4 h-4" />
                ) : (
                  <span className="w-4 h-4 flex items-center justify-center text-xs font-bold">✕</span>
                )}
              </div>
              <div className="flex-1 min-w-0">
                <div className="text-sm text-pf-text-primary">{check.name}</div>
                <div className="text-xs text-pf-text-tertiary">{check.message}</div>
              </div>
            </div>
          ))}
        </div>

        {printer.lastActivity && (
          <div className="text-xs text-pf-text-tertiary mb-3">
            Last activity: {new Date(printer.lastActivity).toLocaleString()}
          </div>
        )}

        <div className="flex gap-2 flex-wrap">
          <Button
            variant="success"
            size="sm"
            onClick={() => onMarkReady(printer.printerId)}
            disabled={!printer.enabled || printer.isReady || isPending}
            iconLeft={<CheckIcon />}
          >
            Mark Ready
          </Button>
          <Button
            variant="secondary"
            size="sm"
            onClick={() => onSkip(printer.printerId)}
            disabled={!printer.enabled || !printer.currentJobName || isPending}
            iconLeft={<SkipForwardIcon />}
          >
            Skip
          </Button>
          <Button
            variant="danger"
            size="sm"
            onClick={() => onCancel(printer.printerId)}
            disabled={!printer.enabled || !printer.currentJobName || isPending}
            iconLeft={<StopIcon />}
          >
            Cancel
          </Button>
        </div>
      </Card.Body>
    </Card>
  );
}
