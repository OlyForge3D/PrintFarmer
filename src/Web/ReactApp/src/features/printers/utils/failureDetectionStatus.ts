import type { BadgeVariant } from '@/common/components/ui/Badge';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';

function ensureSentence(value: string | undefined | null, fallback: string): string {
  const trimmed = value?.trim();
  if (!trimmed) {
    return fallback;
  }

  return /[.!?]$/.test(trimmed) ? trimmed : `${trimmed}.`;
}

function getFailureDetectionAction(
  status: FailureDetectionPrinterStatusDto,
  context: 'misconfigured' | 'error' | 'scan-error'
): string {
  const normalizedReason = status.reason.toLowerCase();

  if (
    normalizedReason.includes('snapshot url request')
    || normalizedReason.includes('reachable from the detection provider')
    || normalizedReason.includes('private to the printer lan')
    || normalizedReason.includes('printer lan')
  ) {
    return 'Make the saved snapshot URL reachable from the configured detection provider, or switch to a camera feed that PrintFarmer can fetch locally.';
  }

  if (
    normalizedReason.includes('snapshot fetch timeout')
    || normalizedReason.includes('could not download the camera snapshot')
  ) {
    return 'Open the latest snapshot and verify PrintFarmer can reach the camera feed quickly enough.';
  }

  if (
    normalizedReason.includes('analysis timed out')
    || normalizedReason.includes('timed out while analyzing')
  ) {
    return 'Check the detection provider and retry after the service recovers.';
  }

  if (normalizedReason.includes('snapshot') || normalizedReason.includes('camera')) {
    return context === 'misconfigured'
      ? 'Add or enable a snapshot camera for this printer.'
      : 'Check the camera feed and verify the saved snapshot URL still responds.';
  }

  if (context === 'scan-error') {
    return 'Check the camera feed or monitoring service before relying on failure alerts.';
  }

  if (
    normalizedReason.includes('obico')
    || normalizedReason.includes('ml service')
    || normalizedReason.includes('contact')
    || normalizedReason.includes('connect')
    || normalizedReason.includes('network')
    || normalizedReason.includes('timeout')
    || normalizedReason.includes('unreachable')
  ) {
    return 'Check the failure-detection service connection, then try again.';
  }

  if (context === 'misconfigured') {
    return 'Review this printer’s failure-detection settings before the next print.';
  }

  return 'Open failure-detection settings and review the latest monitor error.';
}

export function hasFailureDetectionScanTimestamp(value?: string): boolean {
  return Boolean(value && !Number.isNaN(Date.parse(value)));
}

export function getFailureDetectionHealthyOutcomeLabel(state: string, lastAnalyzedAt?: string): string {
  return !hasFailureDetectionScanTimestamp(lastAnalyzedAt)
    ? 'Healthy result without a scan timestamp; current coverage unconfirmed'
    : state === 'monitoring'
      ? 'No failure detected in last recorded scan'
      : 'Historical scan: No failure detected';
}

export function getFailureDetectionScanDisplayState(state: string, lastOutcome: string, lastAnalyzedAt?: string): string {
  return state === 'monitoring' && lastOutcome === 'healthy'
    && !hasFailureDetectionScanTimestamp(lastAnalyzedAt)
    ? 'checking'
    : state;
}

export function getFailureDetectionDisplayState(
  status?: FailureDetectionPrinterStatusDto
): string | undefined {
  if (!status) {
    return undefined;
  }

  if (status.state === 'error' && !status.isPrinting) {
    return undefined;
  }

  return getFailureDetectionScanDisplayState(status.state, status.lastOutcome, status.lastAnalyzedAt);
}

export function getFailureDetectionStateLabel(state?: string, enabled = false): string {
  switch (state) {
    case 'monitoring':
      return 'Guarding';
    case 'idle':
      return 'Ready';
    case 'reservation-held':
      return 'Reservation held';
    case 'unknown':
      return 'Activity unknown';
    case 'misconfigured':
      return 'Needs setup';
    case 'error':
      return 'Monitor error';
    case 'disabled':
      return enabled ? 'Standby' : 'Off';
    default:
      return enabled ? 'Checking' : 'Off';
  }
}

export function getFailureDetectionStateVariant(state?: string, enabled = false): BadgeVariant {
  switch (state) {
    case 'monitoring':
      return 'success';
    case 'idle':
      return 'primary';
    case 'misconfigured':
    case 'reservation-held':
    case 'unknown':
      return 'warning';
    case 'error':
      return 'error';
    case 'disabled':
      return enabled ? 'default' : 'default';
    default:
      return enabled ? 'info' : 'default';
  }
}

/**
 * Shield icon color for a failure-detection state. Red is reserved for `error`.
 *
 * Only semantic tokens are used — never the brand `accent`, which equals
 * success in matrix/ratos and error in voron. The neutral tones (idle,
 * checking, disabled) are guarded against colliding with success, warning and
 * error in every theme by `failureDetectionIconTokens.test.ts`.
 *
 * `enabled` mirrors `getFailureDetectionStateVariant`: an unknown state while
 * enabled is "checking" and an unknown state while disabled is "off". The
 * checking tone stays neutral rather than using `info`, because `info` is
 * within a few ΔE of warning in forge and of success in ratos; the "Checking"
 * label carries the distinction.
 */
export function getFailureDetectionIconColorClass(state?: string, enabled = false): string {
  switch (state) {
    case 'monitoring':
      return 'text-pf-success';
    case 'idle':
      return 'text-pf-status-idle-text';
    case 'misconfigured':
    case 'reservation-held':
    case 'unknown':
      return 'text-pf-warning';
    case 'error':
      return 'text-pf-error';
    case 'disabled':
      return 'text-pf-control-disabled-text';
    default:
      return enabled ? 'text-pf-text-secondary' : 'text-pf-control-disabled-text';
  }
}

export function getFailureDetectionSourceLabel(source?: string): string | null {
  switch (source) {
    case 'pooled':
      return 'Pooled';
    case 'global':
      return 'Global';
    case 'printguard':
      return 'PrintGuard';
    case 'multiple':
      return 'Multiple';
    default:
      return null;
  }
}

export function formatFailureDetectionTimestamp(value?: string): string | null {
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

export function getFailureDetectionDetail(
  status?: FailureDetectionPrinterStatusDto,
  enabled = false
): string {
  const displayState = getFailureDetectionDisplayState(status);

  if (!status) {
    return enabled
      ? 'Checking whether the current print is actively being watched.'
      : 'Failure detection is disabled for this printer.';
  }

  if (!displayState) {
    return enabled
      ? 'Checking whether the current print is actively being watched.'
      : 'Failure detection is disabled for this printer.';
  }

  if (displayState !== 'monitoring') {
    if (displayState === 'checking' && status.lastOutcome === 'healthy') {
      return 'No timestamped scan result is available; current coverage is unconfirmed.';
    }
    return status.reason;
  }

  const scanTime = formatFailureDetectionTimestamp(status.lastAnalyzedAt);
  if (!scanTime) {
    return status.reason;
  }

  switch (status.lastOutcome) {
    case 'failure': {
      const confidence = status.lastConfidence != null
        ? ` • ${Math.round(status.lastConfidence * 100)}% confidence`
        : '';
      return `Last scan ${scanTime} • failure detected${confidence}`;
    }
    case 'healthy':
      return `Last scan ${scanTime} • no failure detected`;
    case 'error':
      return `Last scan ${scanTime} • scan failed — check the camera feed or detection provider`;
    default:
      return `Last scan ${scanTime} • actively watching this print`;
  }
}

export function getFailureDetectionAttentionContent(
  status?: FailureDetectionPrinterStatusDto
): { issue: string; action: string } | null {
  if (!status) {
    return null;
  }

  if (status.state === 'reservation-held') {
    return {
      issue: ensureSentence(status.reason, 'Printer activity is unknown. Obico reservation retained.'),
      action: 'Restore printer connectivity. Only after physically verifying the printer is idle, disable Obico for this printer in printer settings to release the reservation on the next monitoring scan.',
    };
  }

  if (status.state === 'misconfigured') {
    return {
      issue: ensureSentence(status.reason, 'Failure detection is not fully configured.'),
      action: getFailureDetectionAction(status, 'misconfigured'),
    };
  }

  if (status.state === 'error') {
    return {
      issue: ensureSentence(status.reason, 'Failure detection reported an error.'),
      action: getFailureDetectionAction(status, 'error'),
    };
  }

  if (status.state === 'monitoring' && status.lastOutcome === 'error') {
    const scanTime = formatFailureDetectionTimestamp(status.lastAnalyzedAt);

    return {
      issue: scanTime
        ? `The last failure-detection scan failed at ${scanTime}.`
        : 'The last failure-detection scan failed.',
      action: getFailureDetectionAction(status, 'scan-error'),
    };
  }

  return null;
}
