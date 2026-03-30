import { useMemo } from 'react';
import clsx from 'clsx';
import {
  AlertCircleIcon,
  CheckCircleIcon,
  ClockIcon,
  ExternalLinkIcon,
  PauseIcon,
  PlayIcon,
} from '@/common/components/icons/MdiIcons';
import { Badge } from '@/common/components/ui';
import { usePrintSessionTimeline } from '@/common/hooks/useApi';
import type { FailureDetectionEvent, JobStateHistoryDto, StateTransitionDto } from '@/types/api';

interface PrintSessionTimelineProps {
  jobId: string;
  jobLabel: string;
  incidents: FailureDetectionEvent[];
}

type TimelineTone = 'neutral' | 'progress' | 'warning' | 'critical' | 'success';
type TimelineIcon = 'queued' | 'printing' | 'failure' | 'paused' | 'completed';

interface SessionTimelineEvent {
  id: string;
  occurredAt: string;
  sequence: number;
  title: string;
  detail: string;
  tone: TimelineTone;
  icon: TimelineIcon;
  confidenceLabel?: string;
  snapshotUrl?: string;
}

const timelineToneStyles: Record<TimelineTone, { shell: string; icon: string; line: string }> = {
  neutral: {
    shell: 'border-pf-border bg-pf-bg-0',
    icon: 'bg-pf-bg-2 text-pf-text-secondary',
    line: 'bg-pf-border',
  },
  progress: {
    shell: 'border-pf-accent/25 bg-pf-accent-bg/20',
    icon: 'bg-pf-accent/15 text-pf-accent',
    line: 'bg-pf-accent/20',
  },
  warning: {
    shell: 'border-pf-warning/30 bg-pf-warning/10',
    icon: 'bg-pf-warning/15 text-pf-warning-text',
    line: 'bg-pf-warning/25',
  },
  critical: {
    shell: 'border-pf-error/35 bg-pf-error-bg/40',
    icon: 'bg-pf-error/15 text-pf-error',
    line: 'bg-pf-error/25',
  },
  success: {
    shell: 'border-pf-success/30 bg-pf-success-bg/35',
    icon: 'bg-pf-success/15 text-pf-success',
    line: 'bg-pf-success/25',
  },
};

function formatTimelineTimestamp(value: string): string {
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

function formatDuration(seconds?: number): string | null {
  if (seconds == null || seconds < 0) {
    return null;
  }

  if (seconds < 60) {
    return `${seconds}s`;
  }

  const hours = Math.floor(seconds / 3_600);
  const minutes = Math.floor((seconds % 3_600) / 60);

  if (hours > 0) {
    return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
  }

  return `${minutes}m`;
}

function getTransitionPresentation(
  transition: StateTransitionDto
): Omit<SessionTimelineEvent, 'id' | 'occurredAt' | 'sequence'> {
  const toState = transition.toState.trim();
  const normalizedState = toState.toLowerCase();
  const durationLabel = formatDuration(transition.durationInStateSeconds);
  const baseDetail = transition.notes?.trim();

  switch (normalizedState) {
    case 'queued':
      return {
        title: 'Job queued',
        detail: baseDetail ?? (durationLabel ? `Queued for ${durationLabel} before printing.` : 'Job entered the PrintFarmer queue.'),
        tone: 'neutral',
        icon: 'queued',
      };
    case 'printing':
      return {
        title: 'Print started',
        detail: baseDetail ?? 'Printer started the tracked session.',
        tone: 'progress',
        icon: 'printing',
      };
    case 'completed':
      return {
        title: 'Print completed',
        detail: baseDetail ?? 'PrintFarmer recorded the job as completed.',
        tone: 'success',
        icon: 'completed',
      };
    case 'failed':
      return {
        title: 'Print failed',
        detail: baseDetail ?? 'The tracked print ended in a failed state.',
        tone: 'critical',
        icon: 'failure',
      };
    case 'cancelled':
      return {
        title: 'Print cancelled',
        detail: baseDetail ?? 'The tracked print was cancelled before completion.',
        tone: 'warning',
        icon: 'paused',
      };
    case 'paused':
      return {
        title: 'Print paused',
        detail: baseDetail ?? 'The tracked print was paused.',
        tone: 'warning',
        icon: 'paused',
      };
    default:
      return {
        title: `State changed to ${toState}`,
        detail: baseDetail ?? 'PrintFarmer recorded a state transition for this job.',
        tone: 'neutral',
        icon: 'queued',
      };
  }
}

function buildTimelineEvents(
  history: JobStateHistoryDto | undefined,
  incidents: FailureDetectionEvent[]
): SessionTimelineEvent[] {
  const transitionEvents = (history?.transitions ?? []).map((transition, index) => {
    const presentation = getTransitionPresentation(transition);

    return {
      id: `transition-${transition.transitionedAtUtc}-${transition.toState}-${index}`,
      occurredAt: transition.transitionedAtUtc,
      sequence: 0,
      ...presentation,
    } satisfies SessionTimelineEvent;
  });

  const incidentEvents = incidents.flatMap((incident, index) => {
    const confidenceLabel = `${Math.round(incident.confidence * 100)}% confidence`;
    const detectionEvent: SessionTimelineEvent = {
      id: incident.id ?? `failure-${incident.detectedAt}-${index}`,
      occurredAt: incident.detectedAt,
      sequence: 1,
      title: 'Failure incident detected',
      detail: incident.autoPaused
        ? 'Failure detection flagged the session and triggered auto-pause.'
        : 'Failure detection flagged the session for operator review.',
      tone: incident.autoPaused ? 'critical' : 'warning',
      icon: 'failure',
      confidenceLabel,
      snapshotUrl: incident.snapshotUrl,
    };

    if (!incident.autoPaused) {
      return [detectionEvent];
    }

    return [
      detectionEvent,
      {
        id: `${detectionEvent.id}-auto-pause`,
        occurredAt: incident.detectedAt,
        sequence: 2,
        title: 'Print auto-paused',
        detail: 'PrintFarmer attempted to stop the run before the failure could spread.',
        tone: 'critical',
        icon: 'paused',
      } satisfies SessionTimelineEvent,
    ];
  });

  return [...transitionEvents, ...incidentEvents].sort((leftEvent, rightEvent) => {
    const timeDifference = new Date(leftEvent.occurredAt).getTime() - new Date(rightEvent.occurredAt).getTime();
    if (timeDifference !== 0) {
      return timeDifference;
    }

    return leftEvent.sequence - rightEvent.sequence;
  });
}

function TimelineEventIcon({ icon, className }: { icon: TimelineIcon; className?: string }) {
  switch (icon) {
    case 'printing':
      return <PlayIcon className={className} ariaLabel="Print started" />;
    case 'failure':
      return <AlertCircleIcon className={className} ariaLabel="Failure incident" />;
    case 'paused':
      return <PauseIcon className={className} ariaLabel="Print paused" />;
    case 'completed':
      return <CheckCircleIcon className={className} ariaLabel="Print completed" />;
    default:
      return <ClockIcon className={className} ariaLabel="Timeline event" />;
  }
}

export function PrintSessionTimeline({
  jobId,
  jobLabel,
  incidents,
}: PrintSessionTimelineProps) {
  const {
    data: history,
    isLoading,
    isError,
  } = usePrintSessionTimeline(jobId);

  const timelineEvents = useMemo(
    () => buildTimelineEvents(history, incidents),
    [history, incidents]
  );

  return (
    <div className="space-y-3">
      <div className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3">
        <div className="text-xs font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
          Selected session
        </div>
        <div className="mt-1 flex flex-wrap items-center gap-2">
          <div className="text-sm font-medium text-pf-text-primary">{jobLabel}</div>
          <Badge variant="secondary" size="sm">
            {incidents.length} incident{incidents.length === 1 ? '' : 's'}
          </Badge>
        </div>
        <p className="mt-2 text-sm leading-6 text-pf-text-secondary">
          V1 timeline shows the tracked job flow plus failure incidents for this session.
        </p>
      </div>

      {isLoading && (
        <div className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3 text-sm text-pf-text-secondary">
          Loading the selected print session timeline…
        </div>
      )}

      {!isLoading && isError && (
        <div className="rounded-lg border border-pf-warning/25 bg-pf-warning/10 px-4 py-3 text-sm text-pf-text-primary">
          PrintFarmer could not load the tracked job history for this session right now.
        </div>
      )}

      {!isLoading && !isError && timelineEvents.length === 0 && (
        <div className="rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3 text-sm text-pf-text-secondary">
          No tracked timeline events are available for this print session yet.
        </div>
      )}

      {!isLoading && !isError && timelineEvents.length > 0 && (
        <ol className="space-y-3" aria-label={`Print session timeline for ${jobLabel}`}>
          {timelineEvents.map((event, index) => {
            const toneStyles = timelineToneStyles[event.tone];
            const hasConnector = index < timelineEvents.length - 1;

            return (
              <li key={event.id} className="relative pl-12">
                {hasConnector && (
                  <span
                    aria-hidden="true"
                    className={clsx('absolute left-[17px] top-9 h-[calc(100%-1.5rem)] w-px', toneStyles.line)}
                  />
                )}

                <span
                  className={clsx(
                    'absolute left-0 top-1 inline-flex h-9 w-9 items-center justify-center rounded-full',
                    toneStyles.icon
                  )}
                >
                  <TimelineEventIcon icon={event.icon} className="h-4 w-4" />
                </span>

                <div className={clsx('rounded-lg border px-4 py-3', toneStyles.shell)}>
                  <div className="flex flex-wrap items-start justify-between gap-2">
                    <div className="min-w-0">
                      <div className="text-sm font-semibold text-pf-text-primary">{event.title}</div>
                      <div className="mt-1 text-xs font-medium uppercase tracking-[0.14em] text-pf-text-secondary">
                        {formatTimelineTimestamp(event.occurredAt)}
                      </div>
                    </div>
                    {event.confidenceLabel && (
                      <Badge variant={event.tone === 'critical' ? 'error' : 'warning'} size="sm">
                        {event.confidenceLabel}
                      </Badge>
                    )}
                  </div>

                  <p className="mt-2 text-sm leading-6 text-pf-text-primary">{event.detail}</p>

                  {event.snapshotUrl && (
                    <a
                      href={event.snapshotUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="mt-3 inline-flex items-center gap-2 text-sm font-medium text-pf-accent hover:underline underline-offset-2"
                    >
                      Open incident snapshot
                      <ExternalLinkIcon className="h-4 w-4" />
                    </a>
                  )}
                </div>
              </li>
            );
          })}
        </ol>
      )}
    </div>
  );
}
