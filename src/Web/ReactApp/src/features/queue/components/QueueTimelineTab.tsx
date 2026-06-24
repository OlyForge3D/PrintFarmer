import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { Alert, Badge } from "@/common/components/ui";
import { apiClient } from "@/services/api";
import type { QueueStatsDto, TimelineEventDto } from "@/types/api";

interface QueueTimelineTabProps {
  stats: QueueStatsDto | null;
}

interface TimelineEventWithDates {
  event: TimelineEventDto;
  startDate: Date;
  endDate: Date;
}

const TIMELINE_LOOKBACK_HOURS = 24;
const TIMELINE_LIMIT = 200;
const MIN_EVENT_DURATION_SECONDS = 1;
const SECONDS_PER_MINUTE = 60;
const SECONDS_PER_HOUR = 60 * 60;
const PERCENT_MULTIPLIER = 100;

function getStateVariant(state: string): "default" | "primary" | "success" | "warning" | "error" | "info" {
  const normalizedState = state.toLowerCase();
  if (normalizedState.includes("print")) return "success";
  if (normalizedState.includes("queue") || normalizedState.includes("assign") || normalizedState.includes("start")) return "info";
  if (normalizedState.includes("pause")) return "warning";
  if (normalizedState.includes("fail") || normalizedState.includes("cancel") || normalizedState.includes("error")) return "error";
  return "default";
}

function getStateTrackClass(state: string): string {
  const normalizedState = state.toLowerCase();
  if (normalizedState.includes("print")) return "bg-pf-success/70 border border-pf-success";
  if (normalizedState.includes("queue") || normalizedState.includes("assign") || normalizedState.includes("start")) return "bg-pf-accent/70 border border-pf-accent";
  if (normalizedState.includes("pause")) return "bg-pf-warning/70 border border-pf-warning";
  if (normalizedState.includes("fail") || normalizedState.includes("cancel") || normalizedState.includes("error")) return "bg-pf-error/70 border border-pf-error";
  return "bg-pf-bg-2 border border-pf-border";
}

function formatDuration(seconds?: number): string {
  if (!seconds || seconds <= 0) return "—";
  if (seconds < SECONDS_PER_MINUTE) return `${Math.round(seconds)}s`;
  if (seconds < SECONDS_PER_HOUR) return `${Math.round(seconds / SECONDS_PER_MINUTE)}m`;
  const hours = Math.floor(seconds / SECONDS_PER_HOUR);
  const minutes = Math.round((seconds % SECONDS_PER_HOUR) / SECONDS_PER_MINUTE);
  return `${hours}h ${minutes}m`;
}

function formatDateTime(dateValue: string | Date): string {
  const date = dateValue instanceof Date ? dateValue : new Date(dateValue);
  if (Number.isNaN(date.getTime())) return "Unknown";
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  }).format(date);
}

function formatDateTimeShort(dateValue: string): string {
  const date = new Date(dateValue);
  if (Number.isNaN(date.getTime())) return "Unknown";
  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit",
  }).format(date);
}

function buildTimelineWindow() {
  const now = new Date();
  const from = new Date(now);
  from.setHours(now.getHours() - TIMELINE_LOOKBACK_HOURS);
  return { from, to: now };
}

export default function QueueTimelineTab({ stats }: QueueTimelineTabProps) {
  const timelineWindow = useMemo(() => buildTimelineWindow(), []);

  const {
    data: timelineEvents = [],
    isLoading: isTimelineLoading,
    error: timelineError,
  } = useQuery({
    queryKey: ["queue-timeline-events", timelineWindow.from.toISOString(), timelineWindow.to.toISOString()],
    queryFn: () => apiClient.getAnalyticsTimeline(timelineWindow.from, timelineWindow.to, undefined, undefined, TIMELINE_LIMIT),
    staleTime: 10_000,
    refetchInterval: 10_000,
  });

  const { data: queueOverview = [] } = useQuery({
    queryKey: ["queue-overview-timeline"],
    queryFn: () => apiClient.getQueueOverview(),
    staleTime: 30_000,
    refetchInterval: 30_000,
  });

  const parsedEvents = useMemo<TimelineEventWithDates[]>(() => {
    const now = new Date();
    return timelineEvents
      .map((event) => {
        const startDate = new Date(event.enteredAtUtc);
        const inferredEnd = event.exitedAtUtc ? new Date(event.exitedAtUtc) : now;
        const endDate = inferredEnd > startDate ? inferredEnd : new Date(startDate.getTime() + MIN_EVENT_DURATION_SECONDS * 1000);
        if (Number.isNaN(startDate.getTime()) || Number.isNaN(endDate.getTime())) {
          return null;
        }
        return { event, startDate, endDate };
      })
      .filter((entry): entry is TimelineEventWithDates => entry !== null)
      .sort((a, b) => a.startDate.getTime() - b.startDate.getTime());
  }, [timelineEvents]);

  const { groupedEvents, timelineStartMs, timelineEndMs } = useMemo(() => {
    if (parsedEvents.length === 0) {
      return {
        groupedEvents: {} as Record<string, TimelineEventWithDates[]>,
        timelineStartMs: timelineWindow.from.getTime(),
        timelineEndMs: timelineWindow.to.getTime(),
      };
    }

    const grouped = parsedEvents.reduce<Record<string, TimelineEventWithDates[]>>((accumulator, entry) => {
      const printerName = entry.event.printerName || "Unassigned Printer";
      if (!accumulator[printerName]) {
        accumulator[printerName] = [];
      }
      accumulator[printerName].push(entry);
      return accumulator;
    }, {});

    const earliestStart = Math.min(...parsedEvents.map((entry) => entry.startDate.getTime()));
    const latestEnd = Math.max(...parsedEvents.map((entry) => entry.endDate.getTime()));
    const safeEnd = latestEnd > earliestStart ? latestEnd : earliestStart + MIN_EVENT_DURATION_SECONDS * 1000;

    return {
      groupedEvents: grouped,
      timelineStartMs: earliestStart,
      timelineEndMs: safeEnd,
    };
  }, [parsedEvents, timelineWindow.from, timelineWindow.to]);

  const summary = useMemo(() => {
    const activePrinters = queueOverview.filter((printer) => Boolean(printer.currentJobId) || !printer.isAvailable).length;
    const completionCandidates = queueOverview
      .map((printer) => printer.estimatedCompletionTime)
      .filter((value): value is string => Boolean(value))
      .map((value) => new Date(value))
      .filter((value) => !Number.isNaN(value.getTime()))
      .sort((a, b) => a.getTime() - b.getTime());

    const queueCompletion = stats?.estimatedQueueCompletionUtc
      ? new Date(stats.estimatedQueueCompletionUtc)
      : null;
    const staffedCompletion = stats?.staffedCompletionUtc
      ? new Date(stats.staffedCompletionUtc)
      : null;
    const hasQueueCompletion = queueCompletion && !Number.isNaN(queueCompletion.getTime());
    const hasStaffedCompletion = staffedCompletion && !Number.isNaN(staffedCompletion.getTime());
    const staffedAdjustedForNonWorkingHours = Boolean(
      hasQueueCompletion &&
      hasStaffedCompletion &&
      staffedCompletion &&
      queueCompletion &&
      staffedCompletion.getTime() > queueCompletion.getTime(),
    );

    return {
      queued: stats?.totalQueued ?? 0,
      printing: stats?.totalPrinting ?? 0,
      activePrinters,
      nextCompletion: completionCandidates[0] ?? null,
      queueCompletion: hasQueueCompletion ? queueCompletion : null,
      staffedCompletion: hasStaffedCompletion ? staffedCompletion : null,
      staffedAdjustedForNonWorkingHours,
      assumptions: stats?.assumptions ?? null,
    };
  }, [queueOverview, stats]);

  const laneNames = useMemo(() => Object.keys(groupedEvents).sort((a, b) => a.localeCompare(b)), [groupedEvents]);
  const timelineDurationMs = Math.max(timelineEndMs - timelineStartMs, MIN_EVENT_DURATION_SECONDS * 1000);

  if (timelineError) {
    return (
      <Alert type="error">
        Failed to load timeline: {timelineError instanceof Error ? timelineError.message : "Unknown error"}
      </Alert>
    );
  }

  return (
    <div className="space-y-4">
      <section aria-label="Timeline planning summary" className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-6 gap-4">
        <div className="bg-pf-bg-1/95 border border-pf-border rounded-xl p-4 shadow-[0_8px_20px_rgba(0,0,0,0.14)] backdrop-blur-sm">
          <div className="text-pf-text-secondary text-sm font-medium">Queued Jobs</div>
          <div className="text-3xl font-bold text-pf-info tracking-tight">{summary.queued}</div>
        </div>
        <div className="bg-pf-bg-1/95 border border-pf-border rounded-xl p-4 shadow-[0_8px_20px_rgba(0,0,0,0.14)] backdrop-blur-sm">
          <div className="text-pf-text-secondary text-sm font-medium">Currently Printing</div>
          <div className="text-3xl font-bold text-pf-success tracking-tight">{summary.printing}</div>
        </div>
        <div className="bg-pf-bg-1/95 border border-pf-border rounded-xl p-4 shadow-[0_8px_20px_rgba(0,0,0,0.14)] backdrop-blur-sm">
          <div className="text-pf-text-secondary text-sm font-medium">Active Printers</div>
          <div className="text-3xl font-bold text-pf-text-primary tracking-tight">{summary.activePrinters}</div>
        </div>
        <div className="bg-pf-bg-1/95 border border-pf-border rounded-xl p-4 shadow-[0_8px_20px_rgba(0,0,0,0.14)] backdrop-blur-sm">
          <div className="text-pf-text-secondary text-sm font-medium">Next Estimated Completion</div>
          <div className="text-xl font-semibold text-pf-text-primary">
            {summary.nextCompletion ? formatDateTime(summary.nextCompletion) : "No estimate"}
          </div>
        </div>
        <div className="bg-pf-bg-1/95 border border-pf-border rounded-xl p-4 shadow-[0_8px_20px_rgba(0,0,0,0.14)] backdrop-blur-sm">
          <div className="text-pf-text-secondary text-sm font-medium">Queue Completion (Continuous)</div>
          <div className="text-xl font-semibold text-pf-text-primary">
            {summary.queueCompletion ? formatDateTime(summary.queueCompletion) : "No estimate"}
          </div>
        </div>
        <div className="bg-pf-bg-1/95 border border-pf-border rounded-xl p-4 shadow-[0_8px_20px_rgba(0,0,0,0.14)] backdrop-blur-sm">
          <div className="text-pf-text-secondary text-sm font-medium">Queue Completion (Staffed Hours)</div>
          <div className="text-xl font-semibold text-pf-text-primary">
            {summary.staffedCompletion ? formatDateTime(summary.staffedCompletion) : "No estimate"}
          </div>
          {summary.staffedAdjustedForNonWorkingHours && summary.assumptions ? (
            <div className="mt-1 text-xs text-pf-warning">
              Adjusted for non-working hours ({summary.assumptions.workdayStartHourUtc}:00-
              {summary.assumptions.workdayEndHourUtc}:00 UTC, +{summary.assumptions.bedClearMinutes}m bed clear)
            </div>
          ) : null}
        </div>
      </section>

      <section
        aria-label="Queue timeline"
        className="border border-pf-border rounded-xl bg-pf-bg-1/95 overflow-hidden shadow-[0_12px_28px_rgba(0,0,0,0.16)] backdrop-blur-sm"
      >
        <header className="px-4 py-3 border-b border-pf-border bg-pf-bg-0 flex flex-wrap items-center justify-between gap-3">
          <div>
            <h3 className="text-base font-semibold text-pf-text-primary">Farm Timeline</h3>
            <p className="text-sm text-pf-text-secondary">
              Last {TIMELINE_LOOKBACK_HOURS} hours · {parsedEvents.length} event{parsedEvents.length === 1 ? "" : "s"}
            </p>
          </div>
          <div className="text-xs text-pf-text-secondary">
            Window: {formatDateTime(timelineWindow.from)} - {formatDateTime(timelineWindow.to)}
          </div>
        </header>

        {isTimelineLoading ? (
          <div className="p-8 text-center text-pf-text-secondary" role="status" aria-live="polite">
            Loading timeline...
          </div>
        ) : laneNames.length === 0 ? (
          <div className="p-8 text-center">
            <h4 className="text-lg font-semibold text-pf-text-primary">No timeline events</h4>
            <p className="text-pf-text-secondary mt-2">
              Timeline data will appear after jobs move through queue states.
            </p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <div className="min-w-[960px] divide-y divide-pf-border" role="list" aria-label="Timeline lanes by printer">
              {laneNames.map((printerName) => (
                <article key={printerName} className="p-4 space-y-3" aria-labelledby={`timeline-lane-${printerName}`}>
                  <header className="flex items-center justify-between gap-2">
                    <h4 id={`timeline-lane-${printerName}`} className="font-semibold text-pf-text-primary">
                      {printerName}
                    </h4>
                    <span className="text-sm text-pf-text-secondary">
                      {groupedEvents[printerName]?.length ?? 0} item{(groupedEvents[printerName]?.length ?? 0) === 1 ? "" : "s"}
                    </span>
                  </header>

                  <div className="space-y-2">
                    {(groupedEvents[printerName] ?? []).map(({ event, startDate, endDate }) => {
                      const startOffsetMs = startDate.getTime() - timelineStartMs;
                      const durationMs = Math.max(endDate.getTime() - startDate.getTime(), MIN_EVENT_DURATION_SECONDS * 1000);
                      const left = (startOffsetMs / timelineDurationMs) * PERCENT_MULTIPLIER;
                      const width = (durationMs / timelineDurationMs) * PERCENT_MULTIPLIER;
                      const safeWidth = Math.max(width, 0.8);
                      const eventDurationSeconds = event.durationSeconds ?? Math.round(durationMs / 1000);
                      const eventAriaLabel = `${event.jobName} on ${event.printerName || "unassigned printer"}, ${event.state} from ${formatDateTime(
                        event.enteredAtUtc
                      )} to ${event.exitedAtUtc ? formatDateTime(event.exitedAtUtc) : "now"}`;

                      return (
                        <div
                          key={`${event.jobId}-${event.enteredAtUtc}-${event.state}`}
                          className="grid grid-cols-[22rem_minmax(0,1fr)] gap-3 items-center"
                          role="listitem"
                        >
                          <div className="min-w-0">
                            <div className="flex flex-wrap items-center gap-2">
                              <span className="font-medium text-pf-text-primary truncate">{event.jobName}</span>
                              <Badge variant={getStateVariant(event.state)}>{event.state}</Badge>
                            </div>
                            <div className="text-xs text-pf-text-secondary mt-1">
                              {formatDateTimeShort(event.enteredAtUtc)} →{" "}
                              {event.exitedAtUtc ? formatDateTimeShort(event.exitedAtUtc) : "now"} · {formatDuration(eventDurationSeconds)}
                            </div>
                          </div>

                          <div className="relative h-8 rounded-md border border-pf-border bg-pf-bg-0" role="img" aria-label={eventAriaLabel}>
                            <div
                              className={`absolute top-1 bottom-1 rounded-sm ${getStateTrackClass(event.state)}`}
                              style={{ left: `${left}%`, width: `${safeWidth}%` }}
                            />
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </article>
              ))}
            </div>
          </div>
        )}
      </section>
    </div>
  );
}
