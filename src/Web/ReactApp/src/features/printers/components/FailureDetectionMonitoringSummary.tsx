import clsx from 'clsx';
import {
  ExternalLinkIcon,
  ShieldIcon,
} from '@/common/components/icons/MdiIcons';
import { Badge } from '@/common/components/ui';
import type { FailureDetectionEvent, FailureDetectionPrinterStatusDto } from '@/types/api';
import {
  formatFailureDetectionTimestamp,
  getFailureDetectionAttentionContent,
} from '@/features/printers/utils/failureDetectionStatus';

type FailureDetectionMonitoringSummaryVariant = 'compact' | 'detailed';
type FailureDetectionMonitoringSummaryTone = 'critical' | 'attention' | 'healthy' | 'standby';

interface FailureDetectionMonitoringSummaryProps {
  enabled: boolean;
  status?: FailureDetectionPrinterStatusDto;
  recentEvents?: FailureDetectionEvent[];
  printerName?: string;
  variant?: FailureDetectionMonitoringSummaryVariant;
  className?: string;
}

interface ToneStyle {
  row: string;
  icon: string;
}

function formatEventTime(value?: string): string | null {
  if (!value) return null;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return parsed.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
}

function getSummaryTone(
  status: FailureDetectionPrinterStatusDto | undefined,
  latestIncident: FailureDetectionEvent | undefined,
  attention: { issue: string; action: string } | null
): FailureDetectionMonitoringSummaryTone {
  if (latestIncident?.autoPaused || status?.state === 'error') return 'critical';
  if (latestIncident || attention) return 'attention';
  if (status?.state === 'monitoring' && status.lastOutcome === 'healthy') return 'healthy';
  return 'standby';
}

const toneStyles: Record<FailureDetectionMonitoringSummaryTone, ToneStyle> = {
  critical: { row: 'border-pf-error/30 bg-pf-error-bg/60', icon: 'text-pf-error' },
  attention: { row: 'border-pf-warning/30 bg-pf-warning-bg/50', icon: 'text-pf-warning-text' },
  healthy: { row: 'border-pf-success/25 bg-pf-success-bg/40', icon: 'text-pf-success' },
  standby: { row: 'border-pf-border bg-pf-bg-1', icon: 'text-pf-text-tertiary' },
};

function getHeadline(
  enabled: boolean,
  status: FailureDetectionPrinterStatusDto | undefined,
  latestIncident: FailureDetectionEvent | undefined,
  attention: { issue: string; action: string } | null
): string {
  if (latestIncident?.autoPaused) return 'Print auto-paused';
  if (latestIncident) return 'Review required';
  if (status?.state === 'monitoring' && status.lastOutcome === 'healthy') return 'Covered';
  if (status?.state === 'monitoring' && status.lastOutcome === 'failure') return 'Failure detected';
  if (status?.state === 'monitoring' && status.lastOutcome === 'error') return 'Scan failed';
  if (status?.state === 'misconfigured') return 'Setup needed';
  if (status?.state === 'error' || attention) return 'Degraded';
  if (status?.state === 'idle') return 'Standing by';
  return enabled ? 'Connecting' : 'Off';
}

function getSubline(
  status: FailureDetectionPrinterStatusDto | undefined,
  latestIncident: FailureDetectionEvent | undefined
): string | null {
  if (latestIncident) {
    const confidence = Math.round(latestIncident.confidence * 100);
    const time = formatEventTime(latestIncident.detectedAt);
    return time ? `${confidence}% at ${time}` : `${confidence}% confidence`;
  }
  const lastScan = formatFailureDetectionTimestamp(status?.lastAnalyzedAt);
  if (lastScan) return `Last scan ${lastScan}`;
  return null;
}

function getOperatorAction(
  status: FailureDetectionPrinterStatusDto | undefined,
  latestIncident: FailureDetectionEvent | undefined,
  attention: { issue: string; action: string } | null
): string | null {
  if (latestIncident?.autoPaused) return 'Inspect print and verify machine state before resuming.';
  if (latestIncident) return 'Check the live camera — pause or cancel if failure is confirmed.';
  if (attention) return attention.action;
  return null;
}

function getDetailedSummary(
  enabled: boolean,
  status: FailureDetectionPrinterStatusDto | undefined,
  latestIncident: FailureDetectionEvent | undefined,
  printerName: string
): string {
  if (latestIncident) {
    const confidence = Math.round(latestIncident.confidence * 100);
    const time = formatEventTime(latestIncident.detectedAt);
    const timeLabel = time ? ` at ${time}` : '';
    return latestIncident.autoPaused
      ? `${printerName} paused after ${confidence}% confidence detection${timeLabel}.`
      : `${confidence}% confidence detection${timeLabel}. Review before damage spreads.`;
  }
  if (!status) {
    return enabled ? 'Waiting for runtime confirmation.' : 'Failure detection is off.';
  }
  const scanTime = formatFailureDetectionTimestamp(status.lastAnalyzedAt);
  if (status.state === 'monitoring') {
    if (status.lastOutcome === 'healthy') {
      return scanTime ? `Clear at ${scanTime}.` : 'Watching current print.';
    }
    if (status.lastOutcome === 'error') {
      return 'Latest scan failed — check camera connectivity.';
    }
  }
  if (status.state === 'idle') {
    return 'Coverage resumes when next print starts.';
  }
  return status.reason || 'Awaiting status update.';
}

export function FailureDetectionMonitoringSummary({
  enabled,
  status,
  recentEvents = [],
  printerName,
  variant = 'compact',
  className,
}: FailureDetectionMonitoringSummaryProps) {
  if (!enabled && !status && recentEvents.length === 0) return null;

  const resolvedPrinterName = status?.printerName ?? printerName ?? 'This printer';
  const latestIncident = recentEvents[0];
  const attention = getFailureDetectionAttentionContent(status);
  const tone = getSummaryTone(status, latestIncident, attention);
  const style = toneStyles[tone];
  const headline = getHeadline(enabled, status, latestIncident, attention);
  const subline = getSubline(status, latestIncident);
  const operatorAction = getOperatorAction(status, latestIncident, attention);
  const snapshotUrl = latestIncident?.snapshotUrl ?? status?.snapshotUrl;
  const needsAction = tone === 'critical' || tone === 'attention';

  // Compact: slim inline row — icon + headline + subline + badge
  if (variant === 'compact') {
    return (
      <div
        className={clsx('flex items-center gap-2 rounded-lg border px-2.5 py-1.5', style.row, className)}
        {...(latestIncident ? { role: 'status', 'aria-live': 'polite', 'aria-atomic': 'true' } : {})}
      >
        <ShieldIcon className={clsx('h-4 w-4 shrink-0', style.icon)} ariaLabel="Failure detection" />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="text-sm font-medium text-pf-text-primary">{headline}</span>
            {subline && (
              <span className="hidden text-xs text-pf-text-tertiary sm:inline">· {subline}</span>
            )}
          </div>
          {needsAction && operatorAction && (
            <p className="mt-0.5 truncate text-xs leading-tight text-pf-text-secondary">
              {operatorAction}
            </p>
          )}
        </div>
        <Badge
          variant={tone === 'critical' ? 'error' : tone === 'attention' ? 'warning' : tone === 'healthy' ? 'success' : 'default'}
          size="sm"
        >
          {tone === 'critical' ? 'Action' : tone === 'attention' ? 'Review' : tone === 'healthy' ? 'OK' : 'Idle'}
        </Badge>
        {needsAction && snapshotUrl && (
          <a
            href={snapshotUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="shrink-0 text-pf-text-secondary transition-colors hover:text-pf-text-primary"
            title="Open snapshot"
          >
            <ExternalLinkIcon className="h-3.5 w-3.5" />
          </a>
        )}
      </div>
    );
  }

  // Detailed: proportional section — headline + summary + operator action when needed
  const summary = getDetailedSummary(enabled, status, latestIncident, resolvedPrinterName);

  return (
    <section
      className={clsx('rounded-lg border p-3', style.row, className)}
      {...(latestIncident ? { role: 'status', 'aria-live': 'polite', 'aria-atomic': 'true' } : {})}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-start gap-2.5">
          <ShieldIcon className={clsx('mt-0.5 h-4 w-4 shrink-0', style.icon)} ariaLabel="Failure detection" />
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <span className="text-sm font-semibold text-pf-text-primary">{headline}</span>
              <Badge
                variant={tone === 'critical' ? 'error' : tone === 'attention' ? 'warning' : tone === 'healthy' ? 'success' : 'default'}
                size="sm"
              >
                {tone === 'critical' ? 'Action' : tone === 'attention' ? 'Review' : tone === 'healthy' ? 'Covered' : 'Standby'}
              </Badge>
            </div>
            <p className="mt-1 text-sm leading-snug text-pf-text-secondary">{summary}</p>
          </div>
        </div>
        {needsAction && snapshotUrl && (
          <a
            href={snapshotUrl}
            target="_blank"
            rel="noopener noreferrer"
            className={clsx(
              'inline-flex shrink-0 items-center gap-1.5 text-xs font-medium underline-offset-2 hover:underline',
              tone === 'critical' ? 'text-pf-error-text' : 'text-pf-warning-text'
            )}
          >
            Snapshot
            <ExternalLinkIcon className="h-3 w-3" />
          </a>
        )}
      </div>

      {needsAction && operatorAction && (
        <div
          className={clsx(
            'mt-2 rounded border px-2.5 py-2 text-sm leading-snug',
            tone === 'critical'
              ? 'border-pf-error/25 bg-pf-error-bg/40 text-pf-text-primary'
              : 'border-pf-warning/25 bg-pf-warning-bg/35 text-pf-text-primary'
          )}
        >
          <span className="font-medium text-pf-text-secondary">Action: </span>
          {operatorAction}
        </div>
      )}
    </section>
  );
}
