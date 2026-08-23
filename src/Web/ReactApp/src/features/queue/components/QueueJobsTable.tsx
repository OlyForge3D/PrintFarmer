import {
  Fragment,
  forwardRef,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { defaultRangeExtractor, useVirtualizer, type Range } from "@tanstack/react-virtual";
import { Button, ProgressBar, Select, Spinner } from "@/common/components/ui";
import { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";
import { PrintJobPriority, type DispatchUploadProgressDto } from "@/types/api";
import { Download, Clock, Layers, DollarSign, Box, Palette, Timer, FolderOpen, AlertTriangle } from "lucide-react";
import clsx from "clsx";
import { useFleetFilamentCoverage } from "@/features/filament-coverage/hooks";
import { FilamentCoverageBadge } from "@/features/filament-coverage/components/FilamentCoverageBadge";
import type { PrinterFilamentCoverage } from "@/features/filament-coverage/types";

/**
 * Job counts at or under this threshold render every row directly. Above it,
 * `QueueJobsTable` windows rows with `useVirtualizer` so commit duration stays
 * flat as the queue grows instead of scaling linearly with row count
 * (see #1758, following up on the #1735 profiling spike).
 */
export const QUEUE_TABLE_VIRTUALIZATION_THRESHOLD = 20;

const QUEUE_ROW_OVERSCAN = 6;
// Two-row (primary + detail chip) job group at typical viewport widths.
const QUEUE_ROW_ESTIMATED_HEIGHT_PX = 84;
const QUEUE_TABLE_COLUMN_COUNT = 8;

const DUE_SOON_HOURS = 24;

function formatDuration(seconds: number): string {
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);

  if (hours > 0) {
    return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
  }
  return `${minutes}m`;
}

function formatDateTime(iso?: string | null): string {
  if (!iso) return "-";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "-";
  return date.toLocaleString(undefined, {
    year: "2-digit",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function getDeadlineState(deadlineAtUtc: string | undefined, status: string): "none" | "due-soon" | "overdue" {
  if (!deadlineAtUtc) return "none";
  if (["Completed", "Cancelled", "Failed"].includes(status)) return "none";

  const deadlineTs = new Date(deadlineAtUtc).getTime();
  if (Number.isNaN(deadlineTs)) return "none";

  const nowTs = Date.now();
  if (deadlineTs < nowTs) return "overdue";

  const dueSoonThresholdMs = DUE_SOON_HOURS * 60 * 60 * 1000;
  if (deadlineTs - nowTs <= dueSoonThresholdMs) return "due-soon";

  return "none";
}

function getStatusColor(status: string): string {
  switch (status) {
    case "Queued":
      return "bg-pf-info-bg text-pf-info-text";
    case "Assigned":
      return "bg-pf-accent/20 text-pf-accent";
    case "Starting":
      return "bg-pf-warning-bg text-pf-warning-text";
    case "Printing":
      return "bg-pf-success-bg text-pf-success-text";
    case "Paused":
      return "bg-pf-warning-bg text-pf-warning-text";
    case "Completed":
      return "bg-pf-bg-2 text-pf-text-secondary";
    case "Failed":
      return "bg-pf-error-bg text-pf-error-text";
    case "Cancelled":
      return "bg-pf-bg-2 text-pf-text-secondary";
    default:
      return "bg-pf-bg-2 text-pf-text-secondary";
  }
}

function DetailChip({ icon, label, children }: { icon: React.ReactNode; label: string; children: React.ReactNode }) {
  return (
    <span className="inline-flex items-center gap-1 text-xs text-pf-text-secondary" title={label}>
      <span className="text-pf-text-tertiary shrink-0">{icon}</span>
      <span className="text-pf-text-tertiary">{label}:</span>
      <span className="text-pf-text-secondary">{children}</span>
    </span>
  );
}

interface QueueJobRowGroupProps {
  jobWrapper: QueuedPrintJobWithFileMetaDto;
  isLastJob: boolean;
  coverageByPrinterId: ReadonlyMap<string, PrinterFilamentCoverage>;
  dispatchingJobId?: string | null;
  cancelingJobId?: string | null;
  dispatchUploadProgressByJobId?: Record<string, DispatchUploadProgressDto>;
  printProgressByPrinterId?: Record<string, number>;
  printThumbnailByPrinterId?: Record<string, string>;
  onPause?: (jobId: string) => void;
  onResume?: (jobId: string) => void;
  onCancel?: (jobId: string) => void;
  onAbortPrint?: (jobId: string) => void;
  onPriority?: (jobId: string, priority: PrintJobPriority) => void;
  onEdit?: (jobId: string) => void;
  onDispatch?: (jobId: string) => void;
  onSchedule?: (jobId: string) => void;
  /**
   * When set, this row is part of a windowed (virtualized) larger table: the
   * 1-based ARIA row index of this job's primary `<tr>` (the detail `<tr>`
   * is `ariaRowIndexBase + 1`). The table's header `<tr>` is row 1. Omitted
   * on the non-virtualized path, where every row is present in the DOM and
   * native table row counting is already accurate without explicit indices.
   */
  ariaRowIndexBase?: number;
  /** `data-index` for TanStack Virtual's dynamic measurement/DOM mapping. */
  virtualIndex?: number;
}

/**
 * One job's two-row group (primary + detail chips). Extracted so the
 * non-virtualized and virtualized table bodies share a single implementation
 * of the row markup, handlers, and derived display values.
 */
const QueueJobRowGroup = forwardRef<HTMLTableSectionElement, QueueJobRowGroupProps>(
  function QueueJobRowGroup(
    {
      jobWrapper,
      isLastJob,
      coverageByPrinterId,
      dispatchingJobId = null,
      cancelingJobId = null,
      dispatchUploadProgressByJobId,
      printProgressByPrinterId,
      printThumbnailByPrinterId,
      onPause,
      onResume,
      onCancel,
      onAbortPrint,
      onPriority,
      onEdit,
      onDispatch,
      onSchedule,
      ariaRowIndexBase,
      virtualIndex,
    },
    ref,
  ) {
    const job = jobWrapper.job;
    const jobId = job.id;
    const fileName = jobWrapper.gcodeFile?.name || jobWrapper.gcodeFile?.fileName || job.name || "Unknown File";
    const printerName = jobWrapper.assignedPrinter?.name || "Unknown Printer";
    const model = jobWrapper.assignedPrinter?.modelName || "";
    const material = jobWrapper.gcodeFile?.materialType || job.requiredMaterialType || "";
    const status = job.status || "Unknown";
    const priority = job.priority;
    const projectName = job.projectName;

    const livePrinterId = jobWrapper.assignedPrinter?.id;
    const liveProgressRaw = livePrinterId ? printProgressByPrinterId?.[livePrinterId] : undefined;
    const showLiveProgress =
      (status === "Printing" || status === "Paused") && typeof liveProgressRaw === "number";
    const liveProgressPct = showLiveProgress ? Math.min(Math.max(liveProgressRaw!, 0), 100) : 0;
    const liveProgressRounded = Math.round(liveProgressPct);

    const estimatedTimeSeconds = job.estimatedPrintTimeSeconds || jobWrapper.gcodeFile?.estimatedPrintTimeSeconds;
    const estimatedTimeDisplay = estimatedTimeSeconds
      ? formatDuration(estimatedTimeSeconds)
      : "";

    const filamentGrams = job.estimatedFilamentUsageGrams || jobWrapper.gcodeFile?.estimatedFilamentUsageGrams;
    const filamentDisplay = job.filamentName
      ? (
        <span className="inline-flex items-center gap-1" title={`${job.filamentVendor ? job.filamentVendor + ' — ' : ''}${job.filamentName}`}>
          {job.filamentColor && (
            <span
              className="inline-block w-2.5 h-2.5 rounded-full border border-pf-border shrink-0"
              style={{ backgroundColor: job.filamentColor }}
              aria-hidden="true"
            />
          )}
          <span>{job.filamentName}</span>
        </span>
      )
      : filamentGrams
        ? `${filamentGrams.toFixed(1)}g`
        : "";

    const timeDisplay = formatDateTime(job.actualStartTimeUtc ?? job.queuedAtUtc);
    const liveThumbnailUrl =
      (status === "Printing" || status === "Paused") && livePrinterId
        ? printThumbnailByPrinterId?.[livePrinterId]
        : undefined;
    const thumbnailUrl = jobWrapper.gcodeFile?.thumbnailUrl || liveThumbnailUrl;
    const [thumbnailFailedUrl, setThumbnailFailedUrl] = useState<string | undefined>();
    const estimatedCost = job.estimatedCost;
    const deadlineAtUtc = job.deadlineAtUtc;
    const deadlineState = getDeadlineState(deadlineAtUtc, status);
    const deadlineDisplay = formatDateTime(deadlineAtUtc);
    const dispatchProgress = dispatchUploadProgressByJobId?.[jobId];
    const dispatchProgressText = (() => {
      if (!dispatchProgress || dispatchProgress.isCompleted) {
        if (dispatchProgress?.isFailed) return "Failed";
        return "Starting...";
      }
      const stage = dispatchProgress.stage;
      if (stage === "StartingPrint") return "Starting print...";
      if (stage === "Processing") return "Processing...";
      return `Uploading ${dispatchProgress.percentage}%...`;
    })();

    return (
      <tbody
        ref={ref}
        data-index={virtualIndex}
        data-job-id={jobId}
        aria-label={`Print job: ${fileName}${deadlineState === "overdue" ? ", overdue deadline" : deadlineState === "due-soon" ? ", due soon" : ""}`}
        tabIndex={0}
        onClick={() => onEdit?.(jobId)}
        onKeyDown={(e) => {
          if (e.target !== e.currentTarget) return;
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            onEdit?.(jobId);
          }
        }}
        className={clsx(
          "transition-colors cursor-pointer hover:bg-pf-bg-2/50",
          deadlineState === "due-soon" && "bg-pf-warning/5",
          deadlineState === "overdue" && "bg-pf-error/10",
        )}
      >
        {/* Row 1 — Primary: thumbnail, file name, status, printer, copies, priority, actions */}
        <tr
          className="align-middle"
          {...(ariaRowIndexBase !== undefined ? { "aria-rowindex": ariaRowIndexBase } : {})}
        >
          {/* Thumbnail */}
          <td className="px-2 pt-2.5 pb-1 align-middle">
            <div className="flex items-center justify-center">
              {thumbnailUrl && thumbnailUrl !== thumbnailFailedUrl ? (
                <img
                  src={thumbnailUrl}
                  alt=""
                  className="w-10 h-10 min-w-10 rounded object-cover bg-pf-bg-2"
                  onError={() => setThumbnailFailedUrl(thumbnailUrl)}
                />
              ) : (
                <div className="w-10 h-10 min-w-10 rounded bg-pf-bg-2 flex items-center justify-center text-pf-text-tertiary text-xs">
                  —
                </div>
              )}
            </div>
          </td>

          {/* File name */}
          <td className="px-2 py-1 align-middle">
            <div className="font-medium text-pf-text-primary truncate" title={fileName}>
              {fileName}
            </div>
          </td>

          {/* Status + live progress */}
          <td className="px-2 py-1 align-middle">
            <div className="flex flex-col items-center gap-1">
              <span
                className={clsx(
                  "inline-block px-2 py-0.5 rounded-xs text-xs font-medium whitespace-nowrap",
                  getStatusColor(status),
                )}
              >
                {status}
              </span>
              {showLiveProgress && (
                <div className="w-full" title={`${liveProgressRounded}% complete`}>
                  <ProgressBar
                    value={liveProgressPct}
                    ariaLabel="Print progress"
                    showPercent={false}
                    size="xs"
                  />
                  <span className="mt-0.5 block text-center text-[10px] tabular-nums text-pf-text-tertiary">
                    {liveProgressRounded}%
                  </span>
                </div>
              )}
            </div>
          </td>

          {/* Printer */}
          <td className="px-2 py-1 align-middle text-pf-text-secondary">
            <div className="flex items-center gap-1.5 min-w-0">
              <span className="truncate" title={printerName}>
                {printerName}
              </span>
              {(() => {
                const printerId = jobWrapper.assignedPrinter?.id;
                if (!printerId) return null;
                // Issue #1684: an offline printer's last-known coverage
                // status can't be verified (a spool could have been
                // pulled while unreachable), so never surface a runout
                // warning derived from stale data.
                if (jobWrapper.assignedPrinter?.isOnline === false) return null;
                const cov = coverageByPrinterId.get(printerId);
                if (cov?.status !== "runout") return null;
                return (
                  <FilamentCoverageBadge
                    status={cov.status}
                    ariaContext={printerName}
                    compact
                  />
                );
              })()}
            </div>
          </td>

          {/* Copies */}
          <td className="px-2 py-1 align-middle text-center whitespace-nowrap">
            {(job.copies ?? 1) > 1 ? (
              <span className="text-pf-text-primary font-medium text-xs">
                {job.completedCopies ?? 0}/{job.copies}
              </span>
            ) : (
              <span className="text-pf-text-tertiary text-xs">1</span>
            )}
          </td>

          {/* Priority */}
          <td className="px-2 py-1 align-middle" onClick={(e) => e.stopPropagation()}>
            <Select
              value={priority}
              onChange={(e) => onPriority?.(jobId, e.target.value as PrintJobPriority)}
              className="text-xs w-full"
              aria-label="Job priority"
            >
              <option value={PrintJobPriority.Low}>Low</option>
              <option value={PrintJobPriority.Normal}>Normal</option>
              <option value={PrintJobPriority.High}>High</option>
              <option value={PrintJobPriority.Urgent}>Urgent</option>
            </Select>
          </td>

          {/* Deadline */}
          <td className="px-2 py-1 align-middle">
            {deadlineDisplay === "-" ? (
              <span className="text-pf-text-tertiary text-xs">No deadline</span>
            ) : (
              <div className="flex items-center gap-1.5">
                {deadlineState !== "none" && (
                  <AlertTriangle
                    className={clsx(
                      "h-3.5 w-3.5 shrink-0",
                      deadlineState === "overdue" ? "text-pf-error" : "text-pf-warning"
                    )}
                    aria-hidden="true"
                  />
                )}
                <span
                  className={clsx(
                    "text-xs",
                    deadlineState === "overdue" && "text-pf-error font-semibold",
                    deadlineState === "due-soon" && "text-pf-warning font-medium",
                    deadlineState === "none" && "text-pf-text-secondary"
                  )}
                >
                  {deadlineDisplay}
                </span>
                {deadlineState === "overdue" && (
                  <span className="inline-flex items-center rounded-xs bg-pf-error/15 px-1.5 py-0.5 text-[10px] font-semibold text-pf-error">
                    Overdue
                  </span>
                )}
                {deadlineState === "due-soon" && (
                  <span className="inline-flex items-center rounded-xs bg-pf-warning/20 px-1.5 py-0.5 text-[10px] font-semibold text-pf-warning">
                    Due soon
                  </span>
                )}
              </div>
            )}
          </td>

          {/* Actions */}
          <td className="px-2 py-1 align-middle" onClick={(e) => e.stopPropagation()}>
            <div className="flex gap-1.5 flex-wrap">
              {(status === "Queued" || status === "Assigned") && jobWrapper.assignedPrinter && (
                <Button
                  onClick={(e) => {
                    e.stopPropagation();
                    onDispatch?.(jobId);
                  }}
                  variant="primary"
                  size="sm"
                  disabled={dispatchingJobId === jobId}
                >
                  {dispatchingJobId === jobId ? (
                    <span className="flex items-center gap-1">
                      <Spinner size="sm" />
                      {dispatchProgressText}
                    </span>
                  ) : (
                    "Start Print"
                  )}
                </Button>
              )}
              {(status === "Queued" || status === "Assigned") && onSchedule && (
                <Button
                  onClick={(e) => {
                    e.stopPropagation();
                    onSchedule(jobId);
                  }}
                  variant="subtle"
                  size="sm"
                  title="Schedule this job for a specific time"
                >
                  Schedule
                </Button>
              )}
              {status === "Printing" && (
                <Button
                  onClick={(e) => {
                    e.stopPropagation();
                    onPause?.(jobId);
                  }}
                  variant="subtle"
                  size="sm"
                >
                  Pause
                </Button>
              )}
              {status === "Paused" && (
                <Button
                  onClick={(e) => {
                    e.stopPropagation();
                    onResume?.(jobId);
                  }}
                  variant="subtle"
                  size="sm"
                >
                  Resume
                </Button>
              )}
              {(status === "Printing" || status === "Starting" || status === "Paused") && (
                <Button
                  onClick={(e) => {
                    e.stopPropagation();
                    onAbortPrint?.(jobId);
                  }}
                  variant="subtle"
                  size="sm"
                  title="Abort current print attempt, keep job in queue"
                >
                  Abort
                </Button>
              )}
              {status !== "Completed" && status !== "Cancelled" && (
                <Button
                  onClick={(e) => {
                    e.stopPropagation();
                    onCancel?.(jobId);
                  }}
                  variant="danger"
                  size="sm"
                  disabled={cancelingJobId === jobId}
                  title="Cancel job and remove from queue"
                >
                  Cancel
                </Button>
              )}
            </div>
          </td>
        </tr>

        {/* Row 2 — Secondary: detail chips with supplementary info */}
        <tr
          className={clsx(!isLastJob && "border-b border-pf-border")}
          {...(ariaRowIndexBase !== undefined ? { "aria-rowindex": ariaRowIndexBase + 1 } : {})}
        >
          <td aria-hidden="true" />
          <td colSpan={7} className="px-2 pr-4 pb-2.5 pt-0.5">
            <div className="flex items-center gap-x-4 gap-y-1 flex-wrap">
              {projectName && (
                <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-xs font-medium bg-pf-accent/10 text-pf-accent border border-pf-accent/20">
                  <FolderOpen className="h-3 w-3" aria-hidden="true" />
                  {projectName}
                </span>
              )}
              {model && (
                <DetailChip icon={<Box className="h-3 w-3" />} label="Model">
                  {model}
                </DetailChip>
              )}
              {material && (
                <DetailChip icon={<Layers className="h-3 w-3" />} label="Material">
                  {material}
                </DetailChip>
              )}
              {filamentDisplay && (
                <DetailChip icon={<Palette className="h-3 w-3" />} label="Filament">
                  {filamentDisplay}
                </DetailChip>
              )}
              {estimatedTimeDisplay && (
                <DetailChip icon={<Timer className="h-3 w-3" />} label="Est">
                  {estimatedTimeDisplay}
                </DetailChip>
              )}
              {estimatedCost != null && (
                <DetailChip icon={<DollarSign className="h-3 w-3" />} label="Cost">
                  ${estimatedCost.toFixed(2)}
                </DetailChip>
              )}
              {timeDisplay !== "-" && (
                <DetailChip icon={<Clock className="h-3 w-3" />} label="Queued">
                  {timeDisplay}
                </DetailChip>
              )}
              {job.wasSeededFromHistory ? (
                <span
                  role="img"
                  aria-label="Imported"
                  title="Imported from history"
                  className="inline-flex items-center gap-1 text-xs text-pf-text-tertiary"
                >
                  <Download className="h-3 w-3" aria-hidden="true" />
                  <span>Imported</span>
                </span>
              ) : null}
            </div>
          </td>
        </tr>
      </tbody>
    );
  },
);

interface QueueJobsTableFrameProps {
  containerRef?: React.Ref<HTMLDivElement>;
  /** Total logical `<tr>` count (header + all job rows), set only on the virtualized path. */
  ariaRowCount?: number;
  onFocusCapture?: React.FocusEventHandler<HTMLDivElement>;
  onBlurCapture?: React.FocusEventHandler<HTMLDivElement>;
  children: ReactNode;
}

/** Shared outer wrapper/`<table>`/`<colgroup>`/`<thead>` for both table variants. */
function QueueJobsTableFrame({ containerRef, ariaRowCount, onFocusCapture, onBlurCapture, children }: QueueJobsTableFrameProps) {
  return (
    <div
      ref={containerRef}
      className="border border-pf-border rounded-lg bg-pf-bg-1 overflow-hidden"
      {...(onFocusCapture ? { onFocusCapture } : {})}
      {...(onBlurCapture ? { onBlurCapture } : {})}
    >
      <div className="overflow-x-auto">
        <table
          className="w-full table-fixed text-sm"
          aria-label="Print job queue"
          {...(ariaRowCount !== undefined ? { "aria-rowcount": ariaRowCount } : {})}
        >
          <colgroup>
            <col className="w-14" />
            <col />
            <col className="w-20" />
            <col className="w-32" />
            <col className="w-16" />
            <col className="w-28" />
            <col className="w-44" />
            <col className="w-[200px]" />
          </colgroup>
          <thead>
            <tr
              className="bg-pf-bg-2 border-b border-pf-border text-xs font-medium text-pf-text-primary text-left"
              {...(ariaRowCount !== undefined ? { "aria-rowindex": 1 } : {})}
            >
              <th scope="col" className="px-2 py-2.5 font-medium"><span className="sr-only">Thumbnail</span></th>
              <th scope="col" className="px-2 py-2.5 font-medium">File</th>
              <th scope="col" className="px-2 py-2.5 font-medium text-center">Status</th>
              <th scope="col" className="px-2 py-2.5 font-medium">Printer</th>
              <th scope="col" className="px-2 py-2.5 font-medium text-center">Copies</th>
              <th scope="col" className="px-2 py-2.5 font-medium">Priority</th>
              <th scope="col" className="px-2 py-2.5 font-medium">Deadline</th>
              <th scope="col" className="px-2 py-2.5 font-medium">Actions</th>
            </tr>
          </thead>
          {children}
        </table>
      </div>
    </div>
  );
}

type QueueJobsRowSharedProps = Pick<
  QueueJobRowGroupProps,
  | "coverageByPrinterId"
  | "dispatchingJobId"
  | "cancelingJobId"
  | "dispatchUploadProgressByJobId"
  | "printProgressByPrinterId"
  | "printThumbnailByPrinterId"
  | "onPause"
  | "onResume"
  | "onCancel"
  | "onAbortPrint"
  | "onPriority"
  | "onEdit"
  | "onDispatch"
  | "onSchedule"
>;

interface QueueJobsTableVariantProps extends QueueJobsRowSharedProps {
  jobs: QueuedPrintJobWithFileMetaDto[];
}

/** Non-virtualized path: renders every job's row group directly. */
function SmallQueueJobsTable({ jobs, ...rowProps }: QueueJobsTableVariantProps) {
  return (
    <QueueJobsTableFrame>
      {jobs.map((jobWrapper, index) => (
        <QueueJobRowGroup
          key={jobWrapper.job.id}
          jobWrapper={jobWrapper}
          isLastJob={index === jobs.length - 1}
          {...rowProps}
        />
      ))}
    </QueueJobsTableFrame>
  );
}

/**
 * Virtualized path: windows job row groups with `useVirtualizer` so commit
 * duration stays flat as the queue grows (#1758). Rows are kept as real
 * `<tbody>` elements in normal document flow — not absolutely positioned —
 * with spacer `<tbody>` blocks representing the space of scrolled-out rows.
 * This keeps native `<table>` semantics (and the browser's own AX tree)
 * intact instead of reimplementing table roles by hand.
 *
 * Trade-off: browser find-in-page and the app's print stylesheet (which
 * forces `overflow: visible` under `[data-main-content]` so scrolled
 * content prints in full) can't reach rows that are currently unmounted
 * because they're scrolled out of view — the same trade-off already
 * accepted, unremarked, by `PrinterCardGrid`.
 *
 * A focused row is force-kept in the virtualizer's rendered range (see the
 * `rangeExtractor` below) even if it scrolls outside the overscan window,
 * so keyboard focus never silently falls back to `<body>`. Because that can
 * make the rendered range non-contiguous (e.g. a focused row far above the
 * visible window plus the window itself), a spacer `<tbody>` is rendered
 * before *every* row whose `start` offset doesn't immediately follow the
 * previous rendered row's `end` — not just once at the very top — so the
 * gap between non-adjacent rendered rows still occupies correct table
 * space instead of collapsing them together.
 *
 * `getItemKey` is keyed by `job.id` (not the default index-based identity)
 * because the queue refetches continuously and jobs can be inserted,
 * removed, or reordered between renders; without a stable key the
 * virtualizer's per-index measurement cache can apply a stale height/offset
 * to whatever job now occupies that index, causing incorrect spacer math
 * and a visible jump/blank flash mid-scroll.
 */
function VirtualizedQueueJobsTable({ jobs, ...rowProps }: QueueJobsTableVariantProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [scrollElement, setScrollElement] = useState<HTMLElement | null>(null);
  const [scrollMargin, setScrollMargin] = useState(0);
  const [focusedJobId, setFocusedJobId] = useState<string | undefined>();

  useLayoutEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const nextScrollElement = container.closest<HTMLElement>("[data-main-content]");
    setScrollElement(nextScrollElement);
    if (!nextScrollElement) return;

    let animationFrame: number | undefined;
    const measure = () => {
      const containerRect = container.getBoundingClientRect();
      const scrollRect = nextScrollElement.getBoundingClientRect();
      setScrollMargin(containerRect.top - scrollRect.top + nextScrollElement.scrollTop);
    };
    const scheduleMeasure = () => {
      if (animationFrame !== undefined) cancelAnimationFrame(animationFrame);
      animationFrame = requestAnimationFrame(() => {
        animationFrame = undefined;
        measure();
      });
    };

    measure();
    const resizeObserver = new ResizeObserver(scheduleMeasure);
    resizeObserver.observe(container);
    window.addEventListener("resize", scheduleMeasure);

    return () => {
      if (animationFrame !== undefined) cancelAnimationFrame(animationFrame);
      resizeObserver.disconnect();
      window.removeEventListener("resize", scheduleMeasure);
    };
  }, []);

  // TanStack Virtual intentionally exposes mutable measurement methods.
  // eslint-disable-next-line react-hooks/incompatible-library
  const rowVirtualizer = useVirtualizer({
    useFlushSync: false,
    getScrollElement: () => scrollElement,
    count: jobs.length,
    estimateSize: () => QUEUE_ROW_ESTIMATED_HEIGHT_PX,
    overscan: QUEUE_ROW_OVERSCAN,
    scrollMargin,
    // Keyed by job id (not the default index-based identity) so that
    // insertions/removals/reorders in the polled/SignalR-refreshed job list
    // don't apply a stale measurement to whatever job now sits at a given
    // index — see the doc comment above.
    getItemKey: (index) => jobs[index]?.job.id ?? index,
    rangeExtractor: (range: Range) => {
      const indexes = new Set(defaultRangeExtractor(range));
      // Keep a focused row mounted even if it scrolls outside the overscan
      // window (e.g. a mouse-wheel scroll while a row has keyboard focus),
      // so focus never silently falls back to <body>.
      const focusedJobIndex = focusedJobId ? jobs.findIndex((jobWrapper) => jobWrapper.job.id === focusedJobId) : -1;
      if (focusedJobIndex >= 0) {
        indexes.add(focusedJobIndex);
      }
      return [...indexes].sort((left, right) => left - right);
    },
  });

  const virtualRows = rowVirtualizer.getVirtualItems();
  const totalSize = rowVirtualizer.getTotalSize();
  const firstVirtualRow = virtualRows[0];
  const lastVirtualRow = virtualRows[virtualRows.length - 1];
  const paddingTop = firstVirtualRow ? firstVirtualRow.start - scrollMargin : 0;
  const paddingBottom = lastVirtualRow ? totalSize - (lastVirtualRow.end - scrollMargin) : totalSize;

  return (
    <QueueJobsTableFrame
      containerRef={containerRef}
      ariaRowCount={1 + jobs.length * 2}
      onFocusCapture={(event) => {
        const row = (event.target as HTMLElement).closest<HTMLElement>("[data-job-id]");
        if (row?.dataset.jobId) setFocusedJobId(row.dataset.jobId);
      }}
      onBlurCapture={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          setFocusedJobId(undefined);
        }
      }}
    >
      {paddingTop > 0 && (
        <tbody aria-hidden="true">
          <tr>
            <td colSpan={QUEUE_TABLE_COLUMN_COUNT} style={{ height: paddingTop, padding: 0, border: 0 }} />
          </tr>
        </tbody>
      )}
      {virtualRows.map((virtualRow, virtualRowPosition) => {
        const jobWrapper = jobs[virtualRow.index];
        if (!jobWrapper) return null;
        // The rangeExtractor above can force-include a focused row that's
        // outside the visible/overscanned window, making the rendered range
        // non-contiguous. Render a spacer for the gap before this row
        // whenever it doesn't immediately follow the previous rendered row,
        // not just once at the very top — otherwise skipped rows in the
        // middle would collapse to zero height.
        const previousVirtualRow = virtualRows[virtualRowPosition - 1];
        const gapBeforeRow = previousVirtualRow ? virtualRow.start - previousVirtualRow.end : 0;
        return (
          <Fragment key={jobWrapper.job.id}>
            {gapBeforeRow > 0 && (
              <tbody aria-hidden="true">
                <tr>
                  <td colSpan={QUEUE_TABLE_COLUMN_COUNT} style={{ height: gapBeforeRow, padding: 0, border: 0 }} />
                </tr>
              </tbody>
            )}
            <QueueJobRowGroup
              ref={rowVirtualizer.measureElement}
              jobWrapper={jobWrapper}
              isLastJob={virtualRow.index === jobs.length - 1}
              virtualIndex={virtualRow.index}
              ariaRowIndexBase={virtualRow.index * 2 + 2}
              {...rowProps}
            />
          </Fragment>
        );
      })}
      {paddingBottom > 0 && (
        <tbody aria-hidden="true">
          <tr>
            <td colSpan={QUEUE_TABLE_COLUMN_COUNT} style={{ height: paddingBottom, padding: 0, border: 0 }} />
          </tr>
        </tbody>
      )}
    </QueueJobsTableFrame>
  );
}

export interface QueueJobsTableProps {
  jobs: QueuedPrintJobWithFileMetaDto[];
  isLoading?: boolean;
  dispatchingJobId?: string | null;
  cancelingJobId?: string | null;
  dispatchUploadProgressByJobId?: Record<string, DispatchUploadProgressDto>;
  /** Live print progress (0-100) keyed by assigned printer id, from SignalR printer status. */
  printProgressByPrinterId?: Record<string, number>;
  /** Live printer-side thumbnail URL keyed by assigned printer id, from SignalR printer status. */
  printThumbnailByPrinterId?: Record<string, string>;
  onPause?: (jobId: string) => void;
  onResume?: (jobId: string) => void;
  onCancel?: (jobId: string) => void;
  onAbortPrint?: (jobId: string) => void;
  onPriority?: (jobId: string, priority: PrintJobPriority) => void;
  onEdit?: (jobId: string) => void;
  onDispatch?: (jobId: string) => void;
  onSchedule?: (jobId: string) => void;
}

/**
 * Renders the print job queue table. Job lists at or under
 * `QUEUE_TABLE_VIRTUALIZATION_THRESHOLD` render every row directly; larger
 * lists are windowed with `useVirtualizer` so commit duration stays flat as
 * the queue grows (#1758).
 */
export function QueueJobsTable({
  jobs,
  isLoading = false,
  dispatchingJobId = null,
  cancelingJobId = null,
  dispatchUploadProgressByJobId,
  printProgressByPrinterId,
  printThumbnailByPrinterId,
  onPause,
  onResume,
  onCancel,
  onAbortPrint,
  onPriority,
  onEdit,
  onDispatch,
  onSchedule,
}: QueueJobsTableProps) {
  const { data: fleetCoverage } = useFleetFilamentCoverage();
  const coverageByPrinterId = useMemo(
    () => new Map((fleetCoverage?.printers ?? []).map((p) => [p.printerId, p])),
    [fleetCoverage],
  );

  if (isLoading) {
    return (
      <div className="flex justify-center items-center py-12 bg-pf-bg-1 border border-pf-border rounded-lg">
        <div className="text-pf-text-secondary">Loading jobs...</div>
      </div>
    );
  }

  if (jobs.length === 0) {
    return (
      <div className="flex flex-col justify-center items-center py-16 bg-pf-bg-1 border border-pf-border rounded-lg">
        <div className="flex flex-col items-center gap-4 text-center">
          <div className="w-16 h-16 rounded-full bg-pf-bg-2 flex items-center justify-center">
            <span className="text-3xl">📋</span>
          </div>
          <div>
            <h3 className="text-lg font-semibold text-pf-text-primary mb-2">No Print Jobs Queued</h3>
            <p className="text-pf-text-secondary max-w-md">
              Your print queue is empty. Start by uploading or selecting a G-code file to begin printing.
            </p>
          </div>
          <a
            href="/files?type=gcode"
            className="mt-4 px-4 py-2 bg-pf-accent-bg hover:bg-pf-accent-hover text-[var(--pf-on-accent)] rounded-lg font-medium transition-colors"
          >
            Browse G-Code Files
          </a>
        </div>
      </div>
    );
  }

  const rowProps: QueueJobsRowSharedProps = {
    coverageByPrinterId,
    dispatchingJobId,
    cancelingJobId,
    dispatchUploadProgressByJobId,
    printProgressByPrinterId,
    printThumbnailByPrinterId,
    onPause,
    onResume,
    onCancel,
    onAbortPrint,
    onPriority,
    onEdit,
    onDispatch,
    onSchedule,
  };

  if (jobs.length > QUEUE_TABLE_VIRTUALIZATION_THRESHOLD) {
    return <VirtualizedQueueJobsTable jobs={jobs} {...rowProps} />;
  }

  return <SmallQueueJobsTable jobs={jobs} {...rowProps} />;
}
