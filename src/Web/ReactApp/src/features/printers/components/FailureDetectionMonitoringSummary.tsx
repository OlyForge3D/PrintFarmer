import type { ReactNode } from 'react';
import clsx from 'clsx';
import {
  AlertCircleIcon,
  CameraIcon,
  CheckCircleIcon,
  ClockIcon,
  ExternalLinkIcon,
  ShieldIcon,
} from '@/common/components/icons/MdiIcons';
import { Badge } from '@/common/components/ui';
import type { FailureDetectionEvent, FailureDetectionPrinterStatusDto } from '@/types/api';
import {
  formatFailureDetectionTimestamp,
  getFailureDetectionAttentionContent,
  getFailureDetectionSourceLabel,
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

interface SummaryToneStyle {
  shell: string;
  iconWrap: string;
  icon: string;
}

function formatFailureDetectionEventTime(value?: string): string | null {
  if (!value) {
    return null;
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

function getSummaryTone(
  status: FailureDetectionPrinterStatusDto | undefined,
  latestIncident: FailureDetectionEvent | undefined,
  attention: { issue: string; action: string } | null
): FailureDetectionMonitoringSummaryTone {
  if (latestIncident?.autoPaused || status?.state === 'error') {
    return 'critical';
  }

  if (latestIncident || attention) {
    return 'attention';
  }

  if (status?.state === 'monitoring' && status.lastOutcome === 'healthy') {
    return 'healthy';
  }

  return 'standby';
}

const summaryToneStyles: Record<FailureDetectionMonitoringSummaryTone, SummaryToneStyle> = {
  critical: {
    shell: 'border-pf-error/40 bg-gradient-to-r from-pf-error-bg/80 via-pf-error-bg/30 to-pf-bg-0',
    iconWrap: 'bg-pf-error/15 ring-1 ring-pf-error/30',
    icon: 'text-pf-error',
  },
  attention: {
    shell: 'border-pf-warning/40 bg-gradient-to-r from-pf-warning-bg/70 via-pf-warning-bg/20 to-pf-bg-0',
    iconWrap: 'bg-pf-warning/15 ring-1 ring-pf-warning/30',
    icon: 'text-pf-warning-text',
  },
  healthy: {
    shell: 'border-pf-success/35 bg-gradient-to-r from-pf-success-bg/70 via-pf-success-bg/20 to-pf-bg-0',
    iconWrap: 'bg-pf-success/15 ring-1 ring-pf-success/30',
    icon: 'text-pf-success',
  },
  standby: {
    shell: 'border-pf-accent/25 bg-gradient-to-r from-pf-accent-bg/70 via-pf-accent-bg/20 to-pf-bg-0',
    iconWrap: 'bg-pf-accent/12 ring-1 ring-pf-accent/20',
    icon: 'text-pf-accent',
  },
};

function getOperationalHeadline(
  enabled: boolean,
  status: FailureDetectionPrinterStatusDto | undefined,
  latestIncident: FailureDetectionEvent | undefined,
  attention: { issue: string; action: string } | null
): string {
  if (latestIncident?.autoPaused) {
    return 'Print auto-paused';
  }

  if (latestIncident) {
    return 'Operator review required';
  }

  if (status?.state === 'monitoring' && status.lastOutcome === 'healthy') {
    return 'Active coverage';
  }

  if (status?.state === 'monitoring' && status.lastOutcome === 'failure') {
    return 'Failure confirmed';
  }

  if (status?.state === 'monitoring' && status.lastOutcome === 'error') {
    return 'Scan failed';
  }

  if (status?.state === 'misconfigured') {
    return 'Coverage blocked';
  }

  if (status?.state === 'error' || attention) {
    return 'Monitoring degraded';
  }

  if (status?.state === 'idle') {
    return 'Standing by';
  }

  return enabled ? 'Linking to runtime' : 'Failure detection off';
}

function getOperationalSummary(
  enabled: boolean,
  status: FailureDetectionPrinterStatusDto | undefined,
  latestIncident: FailureDetectionEvent | undefined,
  printerName: string
): string {
  const incidentTime = formatFailureDetectionEventTime(latestIncident?.detectedAt);
  if (latestIncident) {
    const confidence = Math.round(latestIncident.confidence * 100);
    const timeLabel = incidentTime ? ` at ${incidentTime}` : '';

    return latestIncident.autoPaused
      ? `${printerName} was paused after a ${confidence}% confidence detection${timeLabel}.`
      : `${confidence}% confidence detection${timeLabel}. Review the live print before damage spreads.`;
  }

  if (!status) {
    return enabled
      ? 'PrintFarmer is waiting for the runtime to confirm whether this printer is covered.'
      : 'Failure detection is turned off for this printer.';
  }

  const scanTime = formatFailureDetectionTimestamp(status.lastAnalyzedAt);

  if (status.state === 'monitoring') {
    switch (status.lastOutcome) {
      case 'healthy':
        return scanTime
          ? `Last scan cleared the print at ${scanTime}.`
          : 'The runtime is watching the current print for failures.';
      case 'failure':
        return scanTime
          ? `The runtime marked the last scan as a failure at ${scanTime}.`
          : 'The runtime marked the latest scan as a failure.';
      case 'error':
        return scanTime
          ? `The latest scan failed at ${scanTime}; camera or ML reachability needs attention.`
          : 'The latest scan failed before the runtime could confirm the print was clear.';
      default:
        return 'The runtime is watching the current print for failures.';
    }
  }

  if (status.state === 'idle') {
    return status.reason || 'Coverage is ready and will resume when the next print starts.';
  }

  return status.reason || 'Failure detection has not reported printer-specific detail yet.';
}

function getOperatorAction(
  status: FailureDetectionPrinterStatusDto | undefined,
  latestIncident: FailureDetectionEvent | undefined,
  attention: { issue: string; action: string } | null
): string | null {
  if (latestIncident?.autoPaused) {
    return 'Inspect the print, clear any loose material, and verify machine state before resuming.';
  }

  if (latestIncident) {
    return 'Check the live camera and pause or cancel the print if the failure is confirmed.';
  }

  if (attention) {
    return attention.action;
  }

  return null;
}

function getLatestResultLabel(
  status: FailureDetectionPrinterStatusDto | undefined,
  latestIncident: FailureDetectionEvent | undefined
): string {
  if (latestIncident) {
    const timeLabel = formatFailureDetectionEventTime(latestIncident.detectedAt);
    const confidence = Math.round(latestIncident.confidence * 100);
    return timeLabel ? `${confidence}% at ${timeLabel}` : `${confidence}% confidence`;
  }

  if (!status) {
    return 'Waiting';
  }

  const scanTime = formatFailureDetectionTimestamp(status.lastAnalyzedAt);

  switch (status.lastOutcome) {
    case 'healthy':
      return scanTime ? `Clear at ${scanTime}` : 'Clear';
    case 'failure':
      return status.lastConfidence != null
        ? `${Math.round(status.lastConfidence * 100)}% confidence`
        : (scanTime ? `Failure at ${scanTime}` : 'Failure detected');
    case 'error':
      return scanTime ? `Scan failed at ${scanTime}` : 'Scan failed';
    case 'none':
      return 'No result yet';
    default:
      return 'Watching';
  }
}

function SummaryStat({
  icon,
  label,
  value,
  className,
}: {
  icon: ReactNode;
  label: string;
  value: string;
  className?: string;
}) {
  return (
    <div
      className={clsx(
        'rounded-lg border border-white/8 bg-black/15 px-3 py-2 backdrop-blur-sm',
        className
      )}
    >
      <div className="flex items-center gap-2 text-[11px] font-semibold uppercase tracking-[0.18em] text-pf-text-tertiary">
        <span className="text-pf-text-secondary">{icon}</span>
        <span>{label}</span>
      </div>
      <div className="mt-1 text-sm font-medium text-pf-text-primary">{value}</div>
    </div>
  );
}

export function FailureDetectionMonitoringSummary({
  enabled,
  status,
  recentEvents = [],
  printerName,
  variant = 'compact',
  className,
}: FailureDetectionMonitoringSummaryProps) {
  if (!enabled && !status && recentEvents.length === 0) {
    return null;
  }

  const resolvedPrinterName = status?.printerName ?? printerName ?? 'This printer';
  const latestIncident = recentEvents[0];
  const attention = getFailureDetectionAttentionContent(status);
  const tone = getSummaryTone(status, latestIncident, attention);
  const toneStyles = summaryToneStyles[tone];
  const sourceLabel = getFailureDetectionSourceLabel(status?.detectionSource) ?? 'Pending';
  const detectionTarget = status?.detectionTarget?.trim() || 'Camera target pending';
  const lastScan = formatFailureDetectionTimestamp(status?.lastAnalyzedAt) ?? 'Waiting';
  const headline = getOperationalHeadline(enabled, status, latestIncident, attention);
  const summary = getOperationalSummary(enabled, status, latestIncident, resolvedPrinterName);
  const operatorAction = getOperatorAction(status, latestIncident, attention);
  const latestResult = getLatestResultLabel(status, latestIncident);
  const snapshotUrl = latestIncident?.snapshotUrl ?? status?.snapshotUrl;

  return (
    <section
      className={clsx(
        'rounded-xl border p-3 text-left shadow-[0_8px_24px_rgba(0,0,0,0.18)]',
        toneStyles.shell,
        variant === 'detailed' ? 'space-y-4 p-4' : 'space-y-3',
        className
      )}
      {...(latestIncident
        ? {
            role: 'status',
            'aria-live': 'polite',
            'aria-atomic': 'true',
          }
        : {})}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="text-[11px] font-semibold uppercase tracking-[0.24em] text-pf-text-secondary">
            Failure detection
          </div>
          <div className="mt-2 flex items-start gap-3">
            <span
              className={clsx(
                'inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-lg backdrop-blur-sm',
                toneStyles.iconWrap
              )}
            >
              <ShieldIcon className={clsx('h-4 w-4', toneStyles.icon)} ariaLabel="Failure detection" />
            </span>
            <div className="min-w-0">
              <div className="text-sm font-semibold uppercase tracking-[0.12em] text-pf-text-primary">
                {headline}
              </div>
              <p className="mt-1 text-sm leading-5 text-pf-text-secondary">{summary}</p>
            </div>
          </div>
        </div>

        <div className="flex shrink-0 flex-wrap justify-end gap-1.5">
          <Badge variant={tone === 'critical' ? 'error' : tone === 'attention' ? 'warning' : tone === 'healthy' ? 'success' : 'info'}>
            {tone === 'critical'
              ? 'Action now'
              : tone === 'attention'
                ? 'Review'
                : tone === 'healthy'
                  ? 'Covered'
                  : 'Standby'}
          </Badge>
        </div>
      </div>

      <div className={clsx('grid gap-2', variant === 'detailed' ? 'sm:grid-cols-2 xl:grid-cols-4' : 'grid-cols-2')}>
        <SummaryStat
          icon={<CameraIcon className="h-3.5 w-3.5" ariaLabel="Coverage source" />}
          label="Source"
          value={sourceLabel}
        />
        <SummaryStat
          icon={<ClockIcon className="h-3.5 w-3.5" ariaLabel="Last scan" />}
          label="Last scan"
          value={lastScan}
        />
        <SummaryStat
          icon={
            latestIncident || status?.lastOutcome === 'failure'
              ? <AlertCircleIcon className="h-3.5 w-3.5" ariaLabel="Latest result" />
              : <CheckCircleIcon className="h-3.5 w-3.5" ariaLabel="Latest result" />
          }
          label="Latest result"
          value={latestResult}
          className={variant === 'compact' ? 'col-span-2' : undefined}
        />
      </div>

      <div className="rounded-lg border border-white/8 bg-black/10 px-3 py-2">
        <div className="text-[11px] font-semibold uppercase tracking-[0.18em] text-pf-text-tertiary">
          Watching
        </div>
        <div className={clsx('mt-1 text-sm text-pf-text-primary', variant === 'compact' && 'truncate')}>
          {detectionTarget}
        </div>
      </div>

      {(attention || operatorAction || snapshotUrl) && (
        <div
          className={clsx(
            'rounded-lg border px-3 py-3',
            tone === 'critical'
              ? 'border-pf-error/30 bg-pf-error-bg/50'
              : 'border-pf-warning/30 bg-pf-warning-bg/45'
          )}
        >
          {attention && (
            <div className="text-sm leading-5 text-pf-text-primary">
              <span className="font-semibold uppercase tracking-[0.12em] text-pf-text-secondary">
                Issue
              </span>
              <div className="mt-1">{attention.issue}</div>
            </div>
          )}

          {operatorAction && (
            <div className={clsx('text-sm leading-5 text-pf-text-primary', attention ? 'mt-3' : '')}>
              <span className="font-semibold uppercase tracking-[0.12em] text-pf-text-secondary">
                Operator action
              </span>
              <div className="mt-1">{operatorAction}</div>
            </div>
          )}

          {snapshotUrl && (
            <a
              href={snapshotUrl}
              target="_blank"
              rel="noopener noreferrer"
              className={clsx(
                'mt-3 inline-flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.16em] underline decoration-transparent underline-offset-4 transition-colors hover:decoration-current',
                tone === 'critical' ? 'text-pf-error-text' : 'text-pf-warning-text'
              )}
            >
              Open latest snapshot
              <ExternalLinkIcon className="h-3.5 w-3.5" />
            </a>
          )}
        </div>
      )}

    </section>
  );
}
