import { Button, Select, Spinner } from "@/common/components/ui";
import clsx from "clsx";
import { AlertTriangle, Clock, DollarSign, FolderOpen, Layers, Palette, Timer } from "lucide-react";
import type { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";
import type { DispatchUploadProgressDto } from "@/types/api";

const DUE_SOON_HOURS = 24;

interface QueueJobsCollectionViewProps {
  jobs: QueuedPrintJobWithFileMetaDto[];
  dispatchingJobId?: string | null;
  cancelingJobId?: string | null;
  dispatchUploadProgressByJobId?: Record<string, DispatchUploadProgressDto>;
  /** Live print progress (0-100) keyed by assigned printer id, from SignalR printer status. */
  printProgressByPrinterId?: Record<string, number>;
  onPause?: (jobId: string) => void;
  onResume?: (jobId: string) => void;
  onCancel?: (jobId: string) => void;
  onAbortPrint?: (jobId: string) => void;
  onPriority?: (jobId: string, priority: number) => void;
  onDispatch?: (jobId: string) => void;
  onSchedule?: (jobId: string) => void;
  onEdit?: (jobId: string) => void;
}

function formatDuration(seconds: number): string {
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (hours > 0) return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
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
  if (deadlineTs < Date.now()) return "overdue";
  return deadlineTs - Date.now() <= DUE_SOON_HOURS * 60 * 60 * 1000 ? "due-soon" : "none";
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
    case "Failed":
      return "bg-pf-error-bg text-pf-error-text";
    default:
      return "bg-pf-bg-2 text-pf-text-secondary";
  }
}

function QueueJobActions({
  jobId,
  status,
  hasAssignedPrinter,
  dispatchingJobId,
  cancelingJobId,
  dispatchUploadProgressByJobId,
  onPause,
  onResume,
  onCancel,
  onAbortPrint,
  onDispatch,
  onSchedule,
}: {
  jobId: string;
  status: string;
  hasAssignedPrinter: boolean;
  dispatchingJobId: string | null;
  cancelingJobId: string | null;
  dispatchUploadProgressByJobId?: Record<string, DispatchUploadProgressDto>;
  onPause?: (jobId: string) => void;
  onResume?: (jobId: string) => void;
  onCancel?: (jobId: string) => void;
  onAbortPrint?: (jobId: string) => void;
  onDispatch?: (jobId: string) => void;
  onSchedule?: (jobId: string) => void;
}) {
  const dispatchProgress = dispatchUploadProgressByJobId?.[jobId];
  const dispatchProgressText = (() => {
    if (!dispatchProgress || dispatchProgress.isCompleted) {
      if (dispatchProgress?.isFailed) return "Failed";
      return "Starting...";
    }
    if (dispatchProgress.stage === "StartingPrint") return "Starting print...";
    if (dispatchProgress.stage === "Processing") return "Processing...";
    return `Uploading ${dispatchProgress.percentage}%...`;
  })();

  return (
    <div className="flex gap-1.5 flex-wrap">
      {(status === "Queued" || status === "Assigned") && hasAssignedPrinter && (
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
        >
          Cancel
        </Button>
      )}
    </div>
  );
}

function QueueJobCommon({
  jobWrapper,
  dispatchingJobId = null,
  cancelingJobId = null,
  dispatchUploadProgressByJobId,
  printProgressByPrinterId,
  onPause,
  onResume,
  onCancel,
  onAbortPrint,
  onPriority,
  onDispatch,
  onSchedule,
  onEdit,
  compact = false,
}: QueueJobsCollectionViewProps & { jobWrapper: QueuedPrintJobWithFileMetaDto; compact?: boolean }) {
  const job = jobWrapper.job;
  const jobId = job.id;
  const fileName = jobWrapper.gcodeFile?.name || jobWrapper.gcodeFile?.fileName || job.name || "Unknown File";
  const printerName = jobWrapper.assignedPrinter?.name || "Unknown Printer";
  const status = job.status || "Unknown";
  const priority = job.priority || 0;
  const projectName = job.projectName;
  const material = jobWrapper.gcodeFile?.materialType || job.requiredMaterialType || "";
  const deadlineAtUtc = job.deadlineAtUtc;
  const deadlineState = getDeadlineState(deadlineAtUtc, status);
  const deadlineDisplay = formatDateTime(deadlineAtUtc);
  const estimatedTimeSeconds = job.estimatedPrintTimeSeconds || jobWrapper.gcodeFile?.estimatedPrintTimeSeconds;
  const estimatedTimeDisplay = estimatedTimeSeconds ? formatDuration(estimatedTimeSeconds) : "—";
  const filamentGrams = job.estimatedFilamentUsageGrams || jobWrapper.gcodeFile?.estimatedFilamentUsageGrams;
  const estimatedCost = job.estimatedCost;
  const thumbnailUrl = jobWrapper.gcodeFile?.thumbnailUrl;

  const livePrinterId = jobWrapper.assignedPrinter?.id;
  const liveProgressRaw = livePrinterId ? printProgressByPrinterId?.[livePrinterId] : undefined;
  const showLiveProgress =
    (status === "Printing" || status === "Paused") && typeof liveProgressRaw === "number";
  const liveProgressPct = showLiveProgress ? Math.min(Math.max(liveProgressRaw!, 0), 100) : 0;
  const liveProgressRounded = Math.round(liveProgressPct);

  return (
    <article
      role="listitem"
      aria-label={`Print job: ${fileName}${deadlineState === "overdue" ? ", overdue deadline" : deadlineState === "due-soon" ? ", due soon" : ""}`}
      tabIndex={0}
      onClick={() => onEdit?.(jobId)}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          onEdit?.(jobId);
        }
      }}
      className={clsx(
        "rounded-xl border border-pf-border bg-pf-bg-1/95 hover:bg-pf-bg-2/60 transition-all duration-200 p-3",
        "shadow-[0_8px_22px_rgba(0,0,0,0.14)] motion-safe:hover:-translate-y-px motion-safe:hover:shadow-[0_14px_28px_rgba(0,0,0,0.2)]",
        deadlineState === "due-soon" && "bg-pf-warning/5",
        deadlineState === "overdue" && "bg-pf-error/10"
      )}
    >
      <div className={clsx("flex gap-3", compact ? "items-start" : "items-start")}>
        <div className="w-14 h-14 shrink-0 rounded bg-pf-bg-2 overflow-hidden flex items-center justify-center">
          {thumbnailUrl ? (
            <img src={thumbnailUrl} alt={`Thumbnail for ${fileName}`} className="w-full h-full object-cover" />
          ) : (
            <span className="text-pf-text-tertiary text-xs">—</span>
          )}
        </div>
        <div className="min-w-0 flex-1 space-y-2">
          <div className="flex items-start justify-between gap-2">
            <div className="min-w-0">
              <h3 className="text-sm font-medium text-pf-text-primary truncate">{fileName}</h3>
              <p className="text-xs text-pf-text-secondary truncate">{printerName}</p>
            </div>
            <span className={clsx("inline-flex px-2 py-0.5 rounded-full text-xs font-medium shrink-0", getStatusColor(status))}>
              {status}
            </span>
          </div>

          {showLiveProgress && (
            <div className="flex items-center gap-2" title={`${liveProgressRounded}% complete`}>
              <div
                className="h-1.5 flex-1 rounded-full bg-pf-bg-2 overflow-hidden"
                role="progressbar"
                aria-valuenow={liveProgressRounded}
                aria-valuemin={0}
                aria-valuemax={100}
                aria-label="Print progress"
              >
                <div
                  className="h-full rounded-full bg-pf-success transition-[width] duration-300"
                  style={{ width: `${liveProgressPct}%` }}
                />
              </div>
              <span className="text-xs tabular-nums text-pf-text-secondary shrink-0">{liveProgressRounded}%</span>
            </div>
          )}

          <div className="flex flex-wrap gap-x-3 gap-y-1 text-xs text-pf-text-secondary">
            <span className="inline-flex items-center gap-1"><Clock className="h-3 w-3" aria-hidden="true" />{deadlineDisplay === "-" ? "No deadline" : deadlineDisplay}</span>
            <span className="inline-flex items-center gap-1"><Timer className="h-3 w-3" aria-hidden="true" />Est {estimatedTimeDisplay}</span>
            {material && <span className="inline-flex items-center gap-1"><Layers className="h-3 w-3" aria-hidden="true" />{material}</span>}
            {filamentGrams != null && <span className="inline-flex items-center gap-1"><Palette className="h-3 w-3" aria-hidden="true" />{filamentGrams.toFixed(1)}g</span>}
            {estimatedCost != null && <span className="inline-flex items-center gap-1"><DollarSign className="h-3 w-3" aria-hidden="true" />${estimatedCost.toFixed(2)}</span>}
            {projectName && <span className="inline-flex items-center gap-1"><FolderOpen className="h-3 w-3" aria-hidden="true" />{projectName}</span>}
            {deadlineState !== "none" && (
              <span className={clsx("inline-flex items-center gap-1 font-medium", deadlineState === "overdue" ? "text-pf-error" : "text-pf-warning")}>
                <AlertTriangle className="h-3 w-3" aria-hidden="true" />
                {deadlineState === "overdue" ? "Overdue" : "Due soon"}
              </span>
            )}
          </div>

          <div className="flex items-center gap-2 flex-wrap" onClick={(e) => e.stopPropagation()}>
            <Select
              value={priority}
              onChange={(e) => onPriority?.(jobId, parseInt(e.target.value, 10))}
              className="text-xs w-28"
              aria-label="Job priority"
            >
              <option value="0">Normal</option>
              <option value="1">High</option>
              <option value="2">Urgent</option>
              <option value="-1">Low</option>
            </Select>
            <QueueJobActions
              jobId={jobId}
              status={status}
              hasAssignedPrinter={Boolean(jobWrapper.assignedPrinter)}
              dispatchingJobId={dispatchingJobId}
              cancelingJobId={cancelingJobId}
              dispatchUploadProgressByJobId={dispatchUploadProgressByJobId}
              onPause={onPause}
              onResume={onResume}
              onCancel={onCancel}
              onAbortPrint={onAbortPrint}
              onDispatch={onDispatch}
              onSchedule={onSchedule}
            />
          </div>
        </div>
      </div>
    </article>
  );
}

export function QueueJobsListView(props: QueueJobsCollectionViewProps) {
  return (
    <div role="list" aria-label="Print job queue list" className="space-y-3">
      {props.jobs.map((job) => (
        <QueueJobCommon key={job.job.id} {...props} jobWrapper={job} compact />
      ))}
    </div>
  );
}

export function QueueJobsCardView(props: QueueJobsCollectionViewProps) {
  return (
    <div role="list" aria-label="Print job queue cards" className="grid grid-cols-1 xl:grid-cols-2 2xl:grid-cols-3 gap-3">
      {props.jobs.map((job) => (
        <QueueJobCommon key={job.job.id} {...props} jobWrapper={job} />
      ))}
    </div>
  );
}
