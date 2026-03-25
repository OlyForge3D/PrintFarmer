import { Modal } from '@/common/components/modals/Modal';
import { Badge } from '@/common/components/ui';
import { ExternalLinkIcon, ShieldIcon } from '@/common/components/icons/MdiIcons';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';
import {
  getFailureDetectionDetail,
  getFailureDetectionDisplayState,
  getFailureDetectionSourceLabel,
  getFailureDetectionStateLabel,
  getFailureDetectionStateVariant,
} from '@/features/printers/utils/failureDetectionStatus';

interface FailureDetectionStatusModalProps {
  isOpen: boolean;
  onClose: () => void;
  enabled: boolean;
  status?: FailureDetectionPrinterStatusDto;
  printerName?: string;
}

function formatFailureDetectionDateTime(value?: string): string | null {
  if (!value) {
    return null;
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  return parsed.toLocaleString([], {
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  });
}

function getFailureDetectionOutcomeLabel(status?: FailureDetectionPrinterStatusDto): string | null {
  if (!status) {
    return null;
  }

  switch (status.lastOutcome) {
    case 'failure':
      return status.lastConfidence != null
        ? `Failure detected (${Math.round(status.lastConfidence * 100)}% confidence)`
        : 'Failure detected';
    case 'healthy':
      return 'No failure detected';
    case 'error':
      return 'Monitoring runtime error';
    case 'none':
      return 'No scans yet';
    default:
      return status.lastOutcome;
  }
}

function getFailureDetectionErrorNextStep(reason: string): string | null {
  const normalizedReason = reason.toLowerCase();

  if (
    normalizedReason.includes('snapshot url request')
    || normalizedReason.includes('reachable from the obico server')
    || normalizedReason.includes('private to the printer lan')
    || normalizedReason.includes('printer lan')
  ) {
    return 'Make the saved snapshot URL reachable from the Obico server network, or switch to a snapshot feed that PrintFarmer can fetch locally.';
  }

  if (
    normalizedReason.includes('snapshot fetch timeout')
    || normalizedReason.includes('could not download the camera snapshot')
  ) {
    return 'Open the latest snapshot and confirm the camera responds from PrintFarmer before relying on failure detection or auto-pause.';
  }

  if (
    normalizedReason.includes('analysis timed out')
    || normalizedReason.includes('timed out while analyzing')
    || normalizedReason.includes('request timeout')
  ) {
    return 'Check the Obico ML service load and camera reachability, then retry once the service is responding normally.';
  }

  return null;
}

function getFailureDetectionNextStep(
  status: FailureDetectionPrinterStatusDto | undefined,
  enabled: boolean
): string {
  const displayState = getFailureDetectionDisplayState(status);

  if (!status) {
    return enabled
      ? 'Give the runtime a moment to report whether this print is actively being watched.'
      : 'Enable failure detection before expecting monitoring or auto-pause coverage on this printer.';
  }

  switch (displayState ?? status.state) {
    case 'monitoring':
      return 'No operator action is needed right now. Failure detection is actively watching the current print.';
    case 'idle':
      return 'No action is needed until this printer starts a new print. Monitoring will resume automatically when a job is active.';
    case 'misconfigured':
      return 'Add or enable a usable camera snapshot feed so failure detection can inspect frames from this printer.';
    case 'error':
      return getFailureDetectionErrorNextStep(status.reason)
        ?? 'Check the Obico ML service connection and camera reachability before relying on failure detection or auto-pause.';
    case 'disabled':
      return enabled
        ? 'Monitoring is standing by. Start a print or verify this printer is eligible for the runtime to begin scanning.'
        : 'Turn on failure detection for this printer if you want the runtime to watch jobs and auto-pause on confirmed failures.';
    default:
      return enabled
        ? 'Wait for the monitoring runtime to confirm current coverage for this printer.'
        : 'Enable failure detection before expecting live monitoring detail for this printer.';
  }
}

function DetailTile({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3">
      <div className="text-xs font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
        {label}
      </div>
      <div className="mt-1 text-sm text-pf-text-primary">{value}</div>
    </div>
  );
}

export function FailureDetectionStatusModal({
  isOpen,
  onClose,
  enabled,
  status,
  printerName,
}: FailureDetectionStatusModalProps) {
  const resolvedPrinterName = status?.printerName ?? printerName ?? 'This printer';
  const displayState = getFailureDetectionDisplayState(status) ?? status?.state;
  const statusLabel = getFailureDetectionStateLabel(displayState, enabled);
  const statusVariant = getFailureDetectionStateVariant(displayState, enabled);
  const detail = getFailureDetectionDetail(status, enabled);
  const reason = status?.reason ?? (enabled
    ? 'The monitoring runtime has not reported printer-specific detail yet.'
    : 'Failure detection is currently disabled for this printer.');
  const sourceLabel = getFailureDetectionSourceLabel(status?.detectionSource)
    ?? (status?.detectionSource === 'none' ? 'Not assigned' : null);
  const detectionTarget = status?.detectionTarget?.trim() || null;
  const lastScan = formatFailureDetectionDateTime(status?.lastAnalyzedAt);
  const lastFailure = formatFailureDetectionDateTime(status?.lastFailureDetectedAt);
  const lastOutcome = getFailureDetectionOutcomeLabel(status);
  const nextStep = getFailureDetectionNextStep(status, enabled);

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Spaghetti detection details"
      size="md"
      titleIcon={<ShieldIcon className="h-5 w-5 text-pf-warning" ariaLabel="Spaghetti detection" />}
      closeAriaLabel={`Close spaghetti detection details for ${resolvedPrinterName}`}
    >
      <div className="space-y-5">
        <div className="rounded-xl border border-pf-border bg-pf-bg-0 p-4">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <div className="text-xs font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
                Printer
              </div>
              <h3 className="mt-1 text-lg font-semibold text-pf-text-primary">
                {resolvedPrinterName}
              </h3>
            </div>
            <Badge variant={statusVariant} size="sm">
              {statusLabel}
            </Badge>
          </div>
          <p className="mt-3 text-sm leading-6 text-pf-text-primary">{detail}</p>
        </div>

        <div className="grid gap-3 sm:grid-cols-2">
          <DetailTile
            label="Coverage source"
            value={sourceLabel ?? (enabled ? 'Runtime is still checking' : 'Disabled')}
          />
          <DetailTile
            label="Watching"
            value={detectionTarget ?? 'Current camera target not reported'}
          />
          {lastScan && <DetailTile label="Last scan" value={lastScan} />}
          {lastOutcome && <DetailTile label="Latest outcome" value={lastOutcome} />}
          {lastFailure && <DetailTile label="Last failure detected" value={lastFailure} />}
          {status?.lastAutoPaused != null && (
            <DetailTile
              label="Auto-pause"
              value={status.lastAutoPaused ? 'Triggered on the last result' : 'Did not trigger on the last result'}
            />
          )}
        </div>

        <section className="space-y-2">
          <h3 className="text-sm font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
            Why this is showing
          </h3>
          <div className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3 text-sm leading-6 text-pf-text-primary">
            {reason}
          </div>
        </section>

        <section className="space-y-2">
          <h3 className="text-sm font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
            Operator next step
          </h3>
          <div className="rounded-lg border border-pf-warning/25 bg-pf-warning/10 px-4 py-3 text-sm leading-6 text-pf-text-primary">
            {nextStep}
          </div>
        </section>

        {status?.snapshotUrl && (
          <div className="rounded-lg border border-pf-accent/20 bg-pf-accent-bg/40 px-4 py-3">
            <a
              href={status.snapshotUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-2 text-sm font-medium text-pf-accent hover:underline underline-offset-2"
            >
              Open latest snapshot
              <ExternalLinkIcon className="h-4 w-4" />
            </a>
          </div>
        )}
      </div>
    </Modal>
  );
}
