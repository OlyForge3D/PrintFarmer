import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
} from "react";
import { useQuery } from "@tanstack/react-query";
import { apiClient } from "@/services/api";
import type { QueueOverviewDto, QueueStatsDto, TimelineEventDto } from "@/types/api";

// ── Constants ─────────────────────────────────────────────────────────────────
const TIMELINE_LIMIT = 500;
const MS_PER_MINUTE = 60_000;
const MS_PER_HOUR = 3_600_000;
const MS_PER_DAY = 24 * MS_PER_HOUR;
const MS_PER_WEEK = 7 * MS_PER_DAY;
const MIN_BAR_PX = 4;
const LANE_H = 48;
const AXIS_H = 36;
const LABEL_W = 192;
const REFRESH_MS = 30_000;
const NOW_TICK_MS = 60_000;
const STAGGER = 22;
const CARD_STAGGER = 65;

// min pixels/hour so bars are never too thin to read
const MIN_PX_PER_H: Record<ZoomLevel, number> = { day: 52, week: 20 };

type ZoomLevel = "day" | "week";
type ViewMode = "printers" | "queue-items";

interface Lane {
  key: string;
  label: string;
  events: ParsedEvent[];
}

interface ParsedEvent {
  event: TimelineEventDto;
  startMs: number;
  endMs: number;
}

interface Tick {
  ms: number;
  label: string;
  major: boolean; // date boundary in week mode
}

// ── Keyframe animations (injected once, transform/opacity only) ───────────────
const GANTT_CSS = `
@media (prefers-reduced-motion: no-preference) {
  @keyframes pfCardIn {
    from { opacity: 0; transform: translateY(10px); }
    to   { opacity: 1; transform: translateY(0);    }
  }
  @keyframes pfLaneIn {
    from { opacity: 0; transform: translateY(5px); }
    to   { opacity: 1; transform: translateY(0);   }
  }
  @keyframes pfBarIn {
    from { opacity: 0; transform: scaleX(0.88); }
    to   { opacity: 1; transform: scaleX(1);    }
  }
  .pf-card-in { animation: pfCardIn 0.42s cubic-bezier(0.16,1,0.3,1) both; }
  .pf-lane-in { animation: pfLaneIn 0.32s cubic-bezier(0.16,1,0.3,1) both; }
  .pf-bar-in  {
    animation: pfBarIn 0.32s cubic-bezier(0.16,1,0.3,1) both;
    transform-origin: left center;
  }
}
`;

// ── Bar color helper (inline styles — CSS vars, no hardcoded hex) ─────────────
function barStyle(state: string): CSSProperties {
  const s = state.toLowerCase();
  if (s.includes("print"))
    return { background: "var(--pf-success)", boxShadow: "0 0 8px var(--pf-success)" };
  if (s.includes("queue") || s.includes("assign") || s.includes("start"))
    return { background: "var(--pf-accent)", opacity: 0.82 };
  if (s.includes("pause"))
    return { background: "var(--pf-warning)", opacity: 0.82 };
  if (s.includes("fail") || s.includes("cancel") || s.includes("error"))
    return { background: "var(--pf-error)", opacity: 0.82 };
  if (s.includes("complet") || s.includes("done") || s.includes("finish"))
    return {
      background: "transparent",
      border: "1px solid var(--pf-border)",
      opacity: 0.55,
    };
  return { background: "var(--pf-bg-2)", border: "1px dashed var(--pf-border)" };
}

// ── Format helpers ─────────────────────────────────────────────────────────────
function fmtRelative(targetMs: number, nowMs: number): string {
  const diff = targetMs - nowMs;
  if (diff <= 0) return "Done";
  const h = Math.floor(diff / MS_PER_HOUR);
  const m = Math.round((diff % MS_PER_HOUR) / MS_PER_MINUTE);
  return h === 0 ? `~${m}m` : `~${h}h ${m}m`;
}

function fmtAbs(d: Date): string {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  }).format(d);
}

function fmtDur(ms: number): string {
  if (ms <= 0) return "—";
  const h = Math.floor(ms / MS_PER_HOUR);
  const m = Math.round((ms % MS_PER_HOUR) / MS_PER_MINUTE);
  return h === 0 ? `${m}m` : `${h}h ${m}m`;
}

// ── Time ticks ────────────────────────────────────────────────────────────────
function buildTicks(startMs: number, endMs: number, zoom: ZoomLevel): Tick[] {
  const interval = zoom === "day" ? MS_PER_HOUR : 4 * MS_PER_HOUR;
  const ticks: Tick[] = [];
  let t = Math.ceil(startMs / interval) * interval;
  while (t <= endMs) {
    const d = new Date(t);
    const major = zoom === "week" && d.getUTCHours() === 0;
    ticks.push({
      ms: t,
      label: major
        ? new Intl.DateTimeFormat(undefined, { weekday: "short", day: "numeric" }).format(d)
        : new Intl.DateTimeFormat(undefined, {
            hour: "2-digit",
            minute: "2-digit",
            hour12: false,
          }).format(d),
      major,
    });
    t += interval;
  }
  return ticks;
}

// ── Non-staffed shading ───────────────────────────────────────────────────────
function buildShadeBlocks(
  winStart: number,
  winEnd: number,
  workStartH: number,
  workEndH: number,
): Array<{ s: number; e: number }> {
  if (workStartH === workEndH) return []; // 24-hour ops → no shading
  const blocks: Array<{ s: number; e: number }> = [];
  const d0 = new Date(winStart);
  d0.setUTCHours(0, 0, 0, 0);
  let day = d0.getTime();

  while (day < winEnd) {
    if (workStartH < workEndH) {
      // Day-shift: shade before work window and after work window each day
      const pre = { s: Math.max(winStart, day), e: Math.min(winEnd, day + workStartH * MS_PER_HOUR) };
      const post = { s: Math.max(winStart, day + workEndH * MS_PER_HOUR), e: Math.min(winEnd, day + MS_PER_DAY) };
      if (pre.e > pre.s) blocks.push(pre);
      if (post.e > post.s) blocks.push(post);
    } else {
      // Night-shift: workEnd < workStart (e.g. 22→06), shade the daytime gap
      const gap = { s: Math.max(winStart, day + workEndH * MS_PER_HOUR), e: Math.min(winEnd, day + workStartH * MS_PER_HOUR) };
      if (gap.e > gap.s) blocks.push(gap);
    }
    day += MS_PER_DAY;
  }
  return blocks;
}

// ── Props ──────────────────────────────────────────────────────────────────────
interface QueueTimelineTabProps {
  stats: QueueStatsDto | null;
  dateFrom: Date | null;
  dateTo: Date | null;
}

// ── StatCard ──────────────────────────────────────────────────────────────────
interface StatCardProps {
  label: string;
  value: string;
  colorClass: string;
  headline?: boolean;
  tooltip?: string;
  delay: number;
}

function StatCard({ label, value, colorClass, headline, tooltip, delay }: StatCardProps) {
  return (
    <div
      title={tooltip}
      style={{
        animationDelay: `${delay}ms`,
        ...(headline && {
          borderLeftColor: "var(--pf-accent)",
          borderLeftWidth: "2px",
          backgroundImage:
            "linear-gradient(135deg, color-mix(in srgb, var(--pf-accent) 8%, transparent) 0%, transparent 55%)",
        }),
      }}
      className="pf-card-in bg-pf-bg-1/95 backdrop-blur-sm border border-pf-border rounded-xl p-4 shadow-[0_8px_20px_rgba(0,0,0,0.14)]"
    >
      <p className="text-sm font-medium text-pf-text-secondary">{label}</p>
      <p className={`font-bold tracking-tight ${headline ? "text-4xl mt-0.5" : "text-3xl"} ${colorClass}`}>
        {value}
      </p>
      {tooltip && headline && (
        <p className="text-xs text-pf-text-secondary mt-1 truncate" aria-label={tooltip}>
          {tooltip}
        </p>
      )}
    </div>
  );
}

// ── Pill button group helper ──────────────────────────────────────────────────
interface PillOption<T extends string> {
  value: T;
  label: string;
}
interface PillGroupProps<T extends string> {
  options: PillOption<T>[];
  active: T;
  onChange: (v: T) => void;
  ariaLabel: string;
}
function PillGroup<T extends string>({ options, active, onChange, ariaLabel }: PillGroupProps<T>) {
  return (
    <div className="flex rounded-lg overflow-hidden border border-pf-border" role="group" aria-label={ariaLabel}>
      {options.map((opt, i) => (
        <button
          key={opt.value}
          type="button"
          onClick={() => onChange(opt.value)}
          className={[
            "px-3 py-1.5 text-sm font-medium transition-colors",
            i > 0 ? "border-l border-pf-border" : "",
            active === opt.value
              ? "bg-pf-accent text-white"
              : "bg-pf-bg-2 text-pf-text-secondary hover:text-pf-text-primary",
          ]
            .filter(Boolean)
            .join(" ")}
        >
          {opt.label}
        </button>
      ))}
    </div>
  );
}

// ── Main component ─────────────────────────────────────────────────────────────
export default function QueueTimelineTab({ stats }: QueueTimelineTabProps) {
  // ── UI state ────────────────────────────────────────────────────────────────
  const [mode, setMode] = useState<ViewMode>("printers");
  const [zoom, setZoom] = useState<ZoomLevel>("day");
  const [winStart, setWinStart] = useState<number>(() => Date.now() - 12 * MS_PER_HOUR);
  const [nowMs, setNowMs] = useState<number>(() => Date.now());
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [chartW, setChartW] = useState(0);

  // ── Refs ────────────────────────────────────────────────────────────────────
  const outerRef = useRef<HTMLDivElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);

  // ── Window metrics ──────────────────────────────────────────────────────────
  const winDur = zoom === "day" ? MS_PER_DAY : MS_PER_WEEK;
  const winEnd = winStart + winDur;
  const minChartPx = (winDur / MS_PER_HOUR) * MIN_PX_PER_H[zoom];
  const totalChartPx = Math.max(chartW || minChartPx, minChartPx);
  const pxPerMs = totalChartPx / winDur;

  // ── Measure scrollable area width ───────────────────────────────────────────
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const ro = new ResizeObserver(([entry]) => {
      setChartW(entry.contentRect.width);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // ── Clock tick ──────────────────────────────────────────────────────────────
  useEffect(() => {
    const id = setInterval(() => setNowMs(Date.now()), NOW_TICK_MS);
    return () => clearInterval(id);
  }, []);

  // ── Fullscreen API ──────────────────────────────────────────────────────────
  useEffect(() => {
    const fn = () => setIsFullscreen(!!document.fullscreenElement);
    document.addEventListener("fullscreenchange", fn);
    return () => document.removeEventListener("fullscreenchange", fn);
  }, []);

  // ── Data: timeline events ───────────────────────────────────────────────────
  // Fetch a 10% wider window so bars at window edges render complete
  const fetchFrom = useMemo(() => new Date(winStart - winDur * 0.1), [winStart, winDur]);
  const fetchTo = useMemo(() => new Date(winEnd + winDur * 0.1), [winEnd, winDur]);

  const { data: rawEvents = [], isLoading } = useQuery<TimelineEventDto[]>({
    queryKey: ["gantt-timeline", fetchFrom.toISOString(), fetchTo.toISOString()],
    queryFn: () => apiClient.getAnalyticsTimeline(fetchFrom, fetchTo, undefined, undefined, TIMELINE_LIMIT),
    staleTime: REFRESH_MS,
    refetchInterval: REFRESH_MS,
  });

  const { data: overview = [] } = useQuery<QueueOverviewDto[]>({
    queryKey: ["gantt-overview"],
    queryFn: () => apiClient.getQueueOverview(),
    staleTime: REFRESH_MS,
    refetchInterval: REFRESH_MS,
  });

  // ── Parse events ─────────────────────────────────────────────────────────────
  const parsed = useMemo<ParsedEvent[]>(() => {
    const fallback = Date.now();
    return rawEvents
      .map((ev): ParsedEvent | null => {
        const s = new Date(ev.enteredAtUtc).getTime();
        if (Number.isNaN(s)) return null;
        const rawE = ev.exitedAtUtc ? new Date(ev.exitedAtUtc).getTime() : fallback;
        return { event: ev, startMs: s, endMs: Math.max(rawE, s + 60_000) };
      })
      .filter((x): x is ParsedEvent => x !== null);
  }, [rawEvents]);

  // ── Build lanes ───────────────────────────────────────────────────────────────
  const lanes = useMemo<Lane[]>(() => {
    const map = new Map<string, ParsedEvent[]>();

    if (mode === "printers") {
      // Seed from overview so idle printers show up as empty rows
      for (const p of overview) {
        if (!map.has(p.printerName)) map.set(p.printerName, []);
      }
      for (const pe of parsed) {
        const k = pe.event.printerName || "Unassigned";
        if (!map.has(k)) map.set(k, []);
        map.get(k)!.push(pe);
      }
    } else {
      for (const pe of parsed) {
        const k = pe.event.jobName || "Unknown Job";
        if (!map.has(k)) map.set(k, []);
        map.get(k)!.push(pe);
      }
    }

    return Array.from(map.entries())
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([key, events]) => ({ key, label: key, events }));
  }, [mode, parsed, overview]);

  // ── Stats summary ─────────────────────────────────────────────────────────────
  const summary = useMemo(() => {
    const active = overview.filter((p) => Boolean(p.currentJobId) || !p.isAvailable).length;
    const staffedMs = stats?.staffedCompletionUtc
      ? new Date(stats.staffedCompletionUtc).getTime()
      : null;
    const queueMs = stats?.estimatedQueueCompletionUtc
      ? new Date(stats.estimatedQueueCompletionUtc).getTime()
      : null;
    const doneMs = staffedMs ?? queueMs;
    const doneTooltip = doneMs
      ? `${staffedMs ? "Staffed hours" : "Continuous"}: ${fmtAbs(new Date(doneMs))}`
      : undefined;
    return {
      queued: stats?.totalQueued ?? 0,
      printing: stats?.totalPrinting ?? 0,
      active,
      doneMs,
      doneTooltip,
      assumptions: stats?.assumptions ?? null,
    };
  }, [overview, stats]);

  // ── Derived layout ─────────────────────────────────────────────────────────
  const ticks = useMemo(() => buildTicks(winStart, winEnd, zoom), [winStart, winEnd, zoom]);

  const shadeBlocks = useMemo(() => {
    if (!summary.assumptions) return [];
    const { workdayStartHourUtc: ws, workdayEndHourUtc: we } = summary.assumptions;
    return buildShadeBlocks(winStart, winEnd, ws, we);
  }, [winStart, winEnd, summary.assumptions]);

  const nowPx = (nowMs - winStart) * pxPerMs;
  const nowVisible = nowPx >= 0 && nowPx <= totalChartPx;
  const toX = useCallback((ms: number) => (ms - winStart) * pxPerMs, [winStart, pxPerMs]);

  // ── Navigation handlers ───────────────────────────────────────────────────
  const goToday = useCallback(() => {
    const half = zoom === "day" ? 12 * MS_PER_HOUR : 3.5 * MS_PER_DAY;
    setWinStart(Date.now() - half);
  }, [zoom]);

  const step = useCallback((dir: -1 | 1) => setWinStart((p) => p + dir * winDur), [winDur]);

  const applyZoom = useCallback(
    (z: ZoomLevel) => {
      setZoom(z);
      const half = z === "day" ? 12 * MS_PER_HOUR : 3.5 * MS_PER_DAY;
      setWinStart(Date.now() - half);
    },
    [],
  );

  const toggleFullscreen = useCallback(() => {
    if (!document.fullscreenElement) {
      outerRef.current?.requestFullscreen().catch(() => {});
    } else {
      document.exitFullscreen().catch(() => {});
    }
  }, []);

  const handleChartKey = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === "ArrowLeft") {
        e.preventDefault();
        setWinStart((p) => p - winDur);
      } else if (e.key === "ArrowRight") {
        e.preventDefault();
        setWinStart((p) => p + winDur);
      }
    },
    [winDur],
  );

  // ── Render ────────────────────────────────────────────────────────────────
  const fsClass = isFullscreen
    ? "bg-pf-bg-0 p-4 h-screen flex flex-col gap-3 overflow-hidden"
    : "space-y-4";

  return (
    <>
      {/* Inject animation keyframes — React 18 hoists <style> to <head> */}
      <style>{GANTT_CSS}</style>

      <div ref={outerRef} className={fsClass}>
        {/* ── Stat cards ──────────────────────────────────────────────────── */}
        <section
          aria-label="Queue statistics"
          className={`grid grid-cols-2 xl:grid-cols-4 gap-3 ${isFullscreen ? "flex-shrink-0" : ""}`}
        >
          <StatCard
            label="Prints Queued"
            value={String(summary.queued)}
            colorClass="text-[var(--pf-info)]"
            delay={0}
          />
          <StatCard
            label="Printing Now"
            value={String(summary.printing)}
            colorClass="text-pf-success"
            delay={CARD_STAGGER}
          />
          <StatCard
            label="Printers Active"
            value={String(summary.active)}
            colorClass="text-pf-text-primary"
            delay={CARD_STAGGER * 2}
          />
          <StatCard
            label="Until All Done"
            value={summary.doneMs ? fmtRelative(summary.doneMs, nowMs) : "—"}
            colorClass="text-pf-accent"
            headline
            tooltip={summary.doneTooltip}
            delay={CARD_STAGGER * 3}
          />
        </section>

        {/* ── Toolbar ─────────────────────────────────────────────────────── */}
        <div
          className={`flex flex-wrap items-center gap-2 ${isFullscreen ? "flex-shrink-0" : ""}`}
          role="toolbar"
          aria-label="Chart controls"
        >
          <PillGroup
            options={[
              { value: "printers" as ViewMode, label: "Printers" },
              { value: "queue-items" as ViewMode, label: "Queue Items" },
            ]}
            active={mode}
            onChange={setMode}
            ariaLabel="View mode"
          />

          <PillGroup
            options={[
              { value: "day" as ZoomLevel, label: "Day" },
              { value: "week" as ZoomLevel, label: "Week" },
            ]}
            active={zoom}
            onChange={applyZoom}
            ariaLabel="Zoom level"
          />

          <button
            type="button"
            onClick={() => step(-1)}
            aria-label="Previous period"
            className="w-8 h-8 flex items-center justify-center bg-pf-bg-2 border border-pf-border rounded-lg text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-1 transition-colors text-lg leading-none"
          >
            ‹
          </button>
          <button
            type="button"
            onClick={goToday}
            className="px-3 h-8 text-sm bg-pf-bg-2 border border-pf-border rounded-lg text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-1 transition-colors"
          >
            Today
          </button>
          <button
            type="button"
            onClick={() => step(1)}
            aria-label="Next period"
            className="w-8 h-8 flex items-center justify-center bg-pf-bg-2 border border-pf-border rounded-lg text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-1 transition-colors text-lg leading-none"
          >
            ›
          </button>

          <span className="flex-1" aria-hidden="true" />

          {/* Live pulse indicator */}
          <span className="flex items-center gap-1.5 text-xs text-pf-text-secondary select-none">
            <span className="relative flex h-2 w-2" aria-hidden="true">
              <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-pf-success opacity-75" />
              <span className="relative inline-flex rounded-full h-2 w-2 bg-pf-success" />
            </span>
            Live
          </span>

          {/* Fullscreen toggle */}
          <button
            type="button"
            onClick={toggleFullscreen}
            aria-label={isFullscreen ? "Exit fullscreen" : "Enter fullscreen"}
            className="w-8 h-8 flex items-center justify-center bg-pf-bg-2 border border-pf-border rounded-lg text-pf-text-secondary hover:text-pf-text-primary transition-colors"
          >
            <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" aria-hidden="true">
              {isFullscreen ? (
                <path d="M5 1v4H1M9 1v4h4M5 13v-4H1M9 13v-4h4" />
              ) : (
                <path d="M1 5V1h4M9 1h4v4M13 9v4H9M5 13H1V9" />
              )}
            </svg>
          </button>
        </div>

        {/* ── Gantt chart ──────────────────────────────────────────────────── */}
        <section
          aria-label={`Queue Gantt — ${mode === "printers" ? "printers" : "queue items"} view`}
          tabIndex={0}
          onKeyDown={handleChartKey}
          className={[
            "border border-pf-border rounded-xl bg-pf-bg-1/95 overflow-hidden",
            "shadow-[0_12px_28px_rgba(0,0,0,0.16)] backdrop-blur-sm",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent",
            isFullscreen ? "flex-1 flex flex-col min-h-0 overflow-hidden" : "",
          ]
            .filter(Boolean)
            .join(" ")}
        >
          {isLoading && lanes.length === 0 ? (
            <div role="status" aria-live="polite" aria-label="Loading timeline">
              <div className="flex">
                <div className="flex-shrink-0 bg-pf-bg-0 border-r border-pf-border" style={{ width: LABEL_W }}>
                  <div style={{ height: AXIS_H }} className="border-b border-pf-border" />
                  {[0, 1, 2].map((i) => (
                    <div
                      key={i}
                      style={{ height: LANE_H }}
                      className={`border-b border-pf-border flex items-center px-3 ${i % 2 === 0 ? "bg-pf-bg-0" : "bg-pf-bg-1/30"}`}
                    >
                      <div className="h-3 rounded animate-pulse bg-pf-bg-2" style={{ width: `${50 + i * 15}%` }} />
                    </div>
                  ))}
                </div>
                <div className="flex-1 overflow-hidden">
                  <div style={{ height: AXIS_H }} className="border-b border-pf-border bg-pf-bg-0" />
                  {[0, 1, 2].map((i) => (
                    <div
                      key={i}
                      style={{ height: LANE_H }}
                      className={`border-b border-pf-border flex items-center px-4 gap-3 ${i % 2 === 0 ? "bg-pf-bg-0" : "bg-pf-bg-1/30"}`}
                    >
                      <div className="h-6 rounded-md animate-pulse bg-pf-bg-2" style={{ width: `${18 + i * 12}%` }} />
                      <div className="h-6 rounded-md animate-pulse bg-pf-bg-2 opacity-60" style={{ width: `${14 + i * 7}%` }} />
                    </div>
                  ))}
                </div>
              </div>
            </div>
          ) : lanes.length === 0 ? (
            <div className="p-14 flex flex-col items-center gap-3 text-center">
              <svg
                width="40"
                height="40"
                viewBox="0 0 40 40"
                fill="none"
                aria-hidden="true"
                className="opacity-30 text-pf-text-secondary"
              >
                <rect x="4" y="10" width="32" height="22" rx="3" stroke="currentColor" strokeWidth="1.5" />
                <path d="M4 16h32" stroke="currentColor" strokeWidth="1.5" />
                <path d="M13 4v6M27 4v6" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
                <path d="M12 24h16M12 29h8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
              </svg>
              <div>
                <p className="text-base font-semibold text-pf-text-primary">No activity in this window</p>
                <p className="text-sm text-pf-text-secondary mt-0.5">
                  Navigate forward or back, or switch to Week zoom.
                </p>
              </div>
            </div>
          ) : (
            <div className={`flex ${isFullscreen ? "flex-1 min-h-0 overflow-hidden" : ""}`}>
              {/* ── Left sticky label column ── */}
              <div
                className="flex-shrink-0 bg-pf-bg-0 border-r border-pf-border z-10"
                style={{ width: LABEL_W }}
              >
                {/* Spacer aligns with time-axis row */}
                <div style={{ height: AXIS_H }} className="border-b border-pf-border" aria-hidden="true" />
                {lanes.map((lane, i) => (
                  <div
                    key={lane.key}
                    style={{ height: LANE_H, animationDelay: `${i * STAGGER}ms` }}
                    className={[
                      "pf-lane-in flex items-center px-3 border-b border-pf-border overflow-hidden",
                      i % 2 === 0 ? "bg-pf-bg-0" : "bg-pf-bg-1/30",
                    ].join(" ")}
                  >
                    <span
                      className="text-sm font-medium text-pf-text-primary truncate w-full"
                      title={lane.label}
                    >
                      {lane.label}
                    </span>
                  </div>
                ))}
              </div>

              {/* ── Scrollable chart area ── */}
              <div
                ref={scrollRef}
                className={`flex-1 overflow-x-auto ${isFullscreen ? "overflow-y-auto" : "overflow-y-hidden"}`}
              >
                {/* Fixed-width inner container */}
                <div style={{ width: totalChartPx, minWidth: totalChartPx }}>
                  {/* ── Time axis ── */}
                  <div
                    style={{ height: AXIS_H }}
                    className="relative border-b border-pf-border bg-pf-bg-0 sticky top-0 z-20"
                    aria-hidden="true"
                  >
                    {ticks.map((tick) => {
                      const x = toX(tick.ms);
                      if (x < -160 || x > totalChartPx + 160) return null;
                      return (
                        <div
                          key={tick.ms}
                          className="absolute top-0 bottom-0 flex items-end pb-1 pl-1"
                          style={{ left: x }}
                        >
                          {/* Vertical gridline in axis */}
                          <div
                            className="absolute top-0 bottom-0 left-0"
                            style={{ width: 1, background: "var(--pf-border)", opacity: tick.major ? 0.5 : 0.3 }}
                          />
                          <span
                            className={[
                              "text-[11px] whitespace-nowrap select-none leading-tight",
                              tick.major
                                ? "font-semibold text-pf-text-primary"
                                : "text-pf-text-secondary",
                            ].join(" ")}
                          >
                            {tick.label}
                          </span>
                        </div>
                      );
                    })}

                    {/* "Now" label on axis */}
                    {nowVisible && (
                      <div
                        className="absolute top-0 z-30 pointer-events-none flex flex-col items-center"
                        style={{ left: nowPx - 16, width: 34 }}
                        aria-hidden="true"
                      >
                        <span className="relative flex h-2 w-2 mt-1 mx-auto">
                          <span
                            className="animate-ping absolute inline-flex h-full w-full rounded-full opacity-60"
                            style={{ background: "var(--pf-accent)" }}
                          />
                          <span
                            className="relative inline-flex h-2 w-2 rounded-full"
                            style={{ background: "var(--pf-accent)" }}
                          />
                        </span>
                        <span
                          className="text-[9px] font-bold whitespace-nowrap mt-0.5 leading-none"
                          style={{ color: "var(--pf-accent)" }}
                        >
                          Now
                        </span>
                      </div>
                    )}
                  </div>

                  {/* ── Chart body ── */}
                  <div
                    className="relative"
                    style={{ height: lanes.length * LANE_H }}
                  >
                    {/* Full-height vertical gridlines */}
                    {ticks.map((tick) => {
                      const x = toX(tick.ms);
                      if (x < 0 || x > totalChartPx) return null;
                      return (
                        <div
                          key={`g-${tick.ms}`}
                          aria-hidden="true"
                          className="absolute top-0 bottom-0 pointer-events-none"
                          style={{
                            left: x,
                            width: 1,
                            background: "var(--pf-border)",
                            opacity: tick.major ? 0.22 : 0.1,
                          }}
                        />
                      );
                    })}

                    {/* Non-staffed shading */}
                    {shadeBlocks.map((block, bi) => (
                      <div
                        key={`sh-${bi}`}
                        aria-hidden="true"
                        className="absolute top-0 bottom-0 pointer-events-none"
                        style={{
                          left: toX(block.s),
                          width: Math.max(0, (block.e - block.s) * pxPerMs),
                          background: "var(--pf-bg-0)",
                          opacity: 0.48,
                          zIndex: 1,
                        }}
                      />
                    ))}

                    {/* "Now" vertical line — behind bars, in front of shading */}
                    {nowVisible && (
                      <div
                        aria-hidden="true"
                        className="absolute top-0 bottom-0 pointer-events-none"
                        style={{ left: nowPx - 5, width: 12, zIndex: 14 }}
                      >
                        <div
                          className="absolute top-0 bottom-0"
                          style={{ left: 5, width: 2, background: "var(--pf-accent)", opacity: 0.85 }}
                        />
                        <div className="absolute top-0 left-0 w-3 h-3 flex items-center justify-center">
                          <div
                            className="absolute inset-0 animate-ping rounded-full opacity-40"
                            style={{ background: "var(--pf-accent)" }}
                          />
                          <div
                            className="relative w-2.5 h-2.5 rounded-full"
                            style={{ background: "var(--pf-accent)" }}
                          />
                        </div>
                      </div>
                    )}

                    {/* Lane rows */}
                    {lanes.map((lane, laneIdx) => (
                      <div
                        key={lane.key}
                        aria-label={`${mode === "printers" ? "Printer" : "Job"}: ${lane.label}`}
                        style={{
                          position: "absolute",
                          top: laneIdx * LANE_H,
                          left: 0,
                          right: 0,
                          height: LANE_H,
                          animationDelay: `${laneIdx * STAGGER}ms`,
                          zIndex: 2,
                        }}
                        className={[
                          "pf-lane-in border-b border-pf-border overflow-hidden",
                          laneIdx % 2 === 0 ? "bg-pf-bg-0" : "bg-pf-bg-1/30",
                        ].join(" ")}
                      >
                        {lane.events.map((pe) => {
                          const bx = toX(pe.startMs);
                          const bw = Math.max((pe.endMs - pe.startMs) * pxPerMs, MIN_BAR_PX);
                          if (bx + bw < 0 || bx > totalChartPx) return null;

                          const tooltip =
                            mode === "printers"
                              ? `${pe.event.jobName} · ${pe.event.state}\n${fmtAbs(new Date(pe.startMs))} → ${pe.event.exitedAtUtc ? fmtAbs(new Date(pe.endMs)) : "Now"} · ${fmtDur(pe.endMs - pe.startMs)}`
                              : `${pe.event.jobName} · ${pe.event.printerName}\n${pe.event.state} · ${fmtAbs(new Date(pe.startMs))} → ${pe.event.exitedAtUtc ? fmtAbs(new Date(pe.endMs)) : "Now"} · ${fmtDur(pe.endMs - pe.startMs)}`;

                          const ariaLbl = `${pe.event.jobName}, ${pe.event.state}, ${fmtAbs(new Date(pe.startMs))} to ${pe.event.exitedAtUtc ? fmtAbs(new Date(pe.endMs)) : "now"}, ${fmtDur(pe.endMs - pe.startMs)}`;

                          const bs = barStyle(pe.event.state);
                          const showLabel =
                            bw > 40 &&
                            bs.background !== "transparent" &&
                            bs.background !== "var(--pf-bg-2)";
                          return (
                            <div
                              key={`${pe.event.jobId}-${pe.event.enteredAtUtc}-${pe.event.state}`}
                              role="img"
                              aria-label={ariaLbl}
                              title={tooltip}
                              style={{
                                position: "absolute",
                                left: bx,
                                width: bw,
                                top: 10,
                                height: 28,
                                zIndex: 10,
                                animationDelay: `${laneIdx * STAGGER + 60}ms`,
                                ...bs,
                              }}
                              className="pf-bar-in rounded-md cursor-default hover:scale-y-110 hover:z-20 transition-transform duration-150 origin-center flex items-center overflow-hidden"
                            >
                              {showLabel && (
                                <span className="truncate text-xs font-medium px-1.5 text-white/90 pointer-events-none select-none drop-shadow-sm">
                                  {pe.event.jobName}
                                </span>
                              )}
                            </div>
                          );
                        })}
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            </div>
          )}

          {/* ── Legend ── */}
          {lanes.length > 0 && (
            <div
              className="px-4 py-2.5 border-t border-pf-border bg-pf-bg-0/80 flex flex-wrap items-center gap-x-5 gap-y-1.5"
              aria-label="Status legend"
            >
              {[
                { label: "Printing", s: { background: "var(--pf-success)", boxShadow: "0 0 4px var(--pf-success)" } as CSSProperties },
                { label: "Queued / Starting", s: { background: "var(--pf-accent)", opacity: 0.82 } as CSSProperties },
                { label: "Paused", s: { background: "var(--pf-warning)", opacity: 0.82 } as CSSProperties },
                { label: "Failed / Cancelled", s: { background: "var(--pf-error)", opacity: 0.82 } as CSSProperties },
                { label: "Complete", s: { background: "transparent", border: "1px solid var(--pf-border)", opacity: 0.55 } as CSSProperties },
                { label: "Unknown", s: { background: "var(--pf-bg-2)", border: "1px dashed var(--pf-border)" } as CSSProperties },
              ].map(({ label, s }) => (
                <span key={label} className="flex items-center gap-1.5">
                  <span
                    className="inline-block w-3 h-3 rounded-sm flex-shrink-0"
                    style={s}
                    aria-hidden="true"
                  />
                  <span className="text-xs text-pf-text-secondary">{label}</span>
                </span>
              ))}
            </div>
          )}
        </section>
      </div>
    </>
  );
}
