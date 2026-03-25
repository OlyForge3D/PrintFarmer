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
    || normalizedReason.includes('reachable from the obico server')
    || normalizedReason.includes('private to the printer lan')
    || normalizedReason.includes('printer lan')
  ) {
    return 'Make the saved snapshot URL reachable from the Obico server network, or switch to a camera feed that PrintFarmer can fetch locally.';
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
    return 'Check the Obico ML service load and retry after the service recovers.';
  }

  if (normalizedReason.includes('snapshot') || normalizedReason.includes('camera')) {
    return context === 'misconfigured'
      ? 'Add or enable a snapshot camera for this printer.'
      : 'Check the camera feed and verify the saved snapshot URL still responds.';
  }

  if (context === 'scan-error') {
    return 'Check the camera feed or monitoring service before relying on automatic pause.';
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

export function getFailureDetectionDisplayState(
  status?: FailureDetectionPrinterStatusDto
): string | undefined {
  if (!status) {
    return undefined;
  }

  if (status.state === 'error' && !status.isPrinting) {
    return undefined;
  }

  return status.state;
}

export function getFailureDetectionStateLabel(state?: string, enabled = false): string {
  switch (state) {
    case 'monitoring':
      return 'Guarding';
    case 'idle':
      return 'Ready';
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
      return 'warning';
    case 'error':
      return 'error';
    case 'disabled':
      return enabled ? 'default' : 'default';
    default:
      return enabled ? 'info' : 'default';
  }
}

export function getFailureDetectionSourceLabel(source?: string): string | null {
  switch (source) {
    case 'pooled':
      return 'Pooled';
    case 'global':
      return 'Global';
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
      const pauseNote = status.lastAutoPaused ? ' • auto-paused' : '';
      return `Last scan ${scanTime} • failure detected${confidence}${pauseNote}`;
    }
    case 'healthy':
      return `Last scan ${scanTime} • no failure detected`;
    case 'error':
      return `Last scan ${scanTime} • scan failed — check the camera feed or ML service`;
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
