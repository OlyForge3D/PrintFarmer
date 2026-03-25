import type { BadgeVariant } from '@/common/components/ui/Badge';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';

export function getFailureDetectionStateLabel(state?: string, enabled = false): string {
  switch (state) {
    case 'monitoring':
      return 'Guarding';
    case 'idle':
      return 'Ready';
    case 'misconfigured':
      return 'Needs setup';
    case 'error':
      return 'Attention';
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
  if (!status) {
    return enabled
      ? 'Checking whether the current print is actively being watched.'
      : 'Failure detection is disabled for this printer.';
  }

  if (status.state !== 'monitoring') {
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
      return `Last scan ${scanTime} • monitoring needs attention`;
    default:
      return `Last scan ${scanTime} • actively watching this print`;
  }
}
