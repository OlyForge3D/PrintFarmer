import type {
  DispatchAttemptOutcome,
  DispatchEscalationLevel,
  JobBlockedReasonCode,
} from '@/types/api';

const OUTCOME_LABELS: Record<DispatchAttemptOutcome, string> = {
  Accepted: 'Accepted',
  Rejected: 'Rejected',
  FailedBeforeStart: 'Failed before start',
  Unknown: 'Outcome unknown',
  InProgress: 'In progress',
  OperatorRecovered: 'Operator recovered',
};

export function dispatchOutcomeLabel(
  outcome: DispatchAttemptOutcome | string | null | undefined
): string {
  if (!outcome) {
    return 'Unknown';
  }
  return OUTCOME_LABELS[outcome as DispatchAttemptOutcome] ?? outcome;
}

const ESCALATION_LABELS: Record<DispatchEscalationLevel, string> = {
  None: 'None',
  Warning: 'Warning',
  Operational: 'Operational',
  Critical: 'Critical',
  HardLimit: 'Hard limit',
};

export function escalationLabel(level: DispatchEscalationLevel | string | null | undefined): string {
  if (!level) {
    return 'None';
  }
  return ESCALATION_LABELS[level as DispatchEscalationLevel] ?? level;
}

export function isRecoveryBlocked(
  job: { status?: string; blockedReasonCode?: JobBlockedReasonCode | null } | null | undefined
): boolean {
  return job?.blockedReasonCode === 'OperatorRecoveryRequired';
}

export function formatClaimAge(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) {
    return 'unknown';
  }
  const total = Math.floor(seconds);
  if (total < 60) {
    return `${total}s`;
  }
  const minutes = Math.floor(total / 60);
  if (minutes < 60) {
    return `${minutes}m`;
  }
  const hours = Math.floor(minutes / 60);
  const remMinutes = minutes % 60;
  if (hours < 24) {
    return remMinutes ? `${hours}h ${remMinutes}m` : `${hours}h`;
  }
  const days = Math.floor(hours / 24);
  const remHours = hours % 24;
  return remHours ? `${days}d ${remHours}h` : `${days}d`;
}

export function shortId(id: string | null | undefined): string {
  if (!id) {
    return '—';
  }
  return id.length > 8 ? id.slice(0, 8) : id;
}
