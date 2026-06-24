import { Button, Select, Spinner } from "@/common/components/ui";
import { useState, useRef, useCallback } from "react";
import { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";
import type { DispatchUploadProgressDto } from "@/types/api";
import { Download, GripVertical, Clock, Layers, DollarSign, Box, Palette, Timer, FolderOpen, AlertTriangle } from "lucide-react";
import clsx from "clsx";

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

function DetailChip({ icon, label, children }: { icon: React.ReactNode; label: string; children: React.ReactNode }) {
  return (
    <span className="inline-flex items-center gap-1 text-xs text-pf-text-secondary" title={label}>
      <span className="text-pf-text-tertiary shrink-0">{icon}</span>
      <span className="text-pf-text-tertiary">{label}:</span>
      <span className="text-pf-text-secondary">{children}</span>
    </span>
  );
}

export interface QueueJobsTableProps {
  jobs: QueuedPrintJobWithFileMetaDto[];
  isLoading?: boolean;
  dispatchingJobId?: string | null;
  cancelingJobId?: string | null;
  dispatchUploadProgressByJobId?: Record<string, DispatchUploadProgressDto>;
  onPause?: (jobId: string) => void;
  onResume?: (jobId: string) => void;
  onCancel?: (jobId: string) => void;
  onAbortPrint?: (jobId: string) => void;
  onPriority?: (jobId: string, priority: number) => void;
  onEdit?: (jobId: string) => void;
  onDispatch?: (jobId: string) => void;
  onSchedule?: (jobId: string) => void;
  onReorder?: (moves: { jobId: string; newPosition: number }[]) => void;
}

export function QueueJobsTable({
  jobs,
  isLoading = false,
  dispatchingJobId = null,
  cancelingJobId = null,
  dispatchUploadProgressByJobId,
  onPause,
  onResume,
  onCancel,
  onAbortPrint,
  onPriority,
  onEdit,
  onDispatch,
  onSchedule,
  onReorder,
}: QueueJobsTableProps) {
  const [dragIndex, setDragIndex] = useState<number | null>(null);
  const [dropIndex, setDropIndex] = useState<number | null>(null);
  const dragCounter = useRef(0);

  const handleDragStart = useCallback((e: React.DragEvent<HTMLDivElement>, index: number) => {
    setDragIndex(index);
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/plain", String(index));
    requestAnimationFrame(() => {
      (e.target as HTMLElement).style.opacity = "0.4";
    });
  }, []);

  const handleDragEnd = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    (e.target as HTMLElement).style.opacity = "1";
    setDragIndex(null);
    setDropIndex(null);
    dragCounter.current = 0;
  }, []);

  const handleDragEnter = useCallback((e: React.DragEvent<HTMLDivElement>, index: number) => {
    e.preventDefault();
    dragCounter.current++;
    setDropIndex(index);
  }, []);

  const handleDragLeave = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    dragCounter.current--;
    if (dragCounter.current === 0) {
      setDropIndex(null);
    }
  }, []);

  const handleDragOver = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
  }, []);

  const handleDrop = useCallback((e: React.DragEvent<HTMLDivElement>, targetIndex: number) => {
    e.preventDefault();
    dragCounter.current = 0;
    setDropIndex(null);

    if (dragIndex === null || dragIndex === targetIndex) {
      setDragIndex(null);
      return;
    }

    const reordered = [...jobs];
    const [moved] = reordered.splice(dragIndex, 1);
    reordered.splice(targetIndex, 0, moved);

    const moves = reordered.map((j, i) => ({
      jobId: j.job.id,
      newPosition: i + 1,
    }));

    setDragIndex(null);
    onReorder?.(moves);
  }, [dragIndex, jobs, onReorder]);

  const getStatusColor = (status: string) => {
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
  };

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
            className="mt-4 px-4 py-2 bg-pf-accent hover:bg-pf-accent-dark text-white rounded-lg font-medium transition-colors"
          >
            Browse G-Code Files
          </a>
        </div>
      </div>
    );
  }

  return (
    <div className="border border-pf-border rounded-lg bg-pf-bg-1 overflow-hidden" role="list" aria-label="Print job queue">
      {/* Header */}
      <div className="grid grid-cols-[40px_56px_1fr_auto_auto_auto_auto_auto_auto] items-center gap-x-2 px-2 py-2.5 bg-pf-bg-2 border-b border-pf-border text-xs font-medium text-pf-text-primary">
        <span className="sr-only">Reorder</span>
        <span className="sr-only">Thumbnail</span>
        <span className="px-2">File</span>
        <span className="px-2 w-20 text-center">Status</span>
        <span className="px-2 w-32">Printer</span>
        <span className="px-2 w-16 text-center">Copies</span>
        <span className="px-2 w-28">Priority</span>
        <span className="px-2 w-44">Deadline</span>
        <span className="px-2 min-w-[180px]">Actions</span>
      </div>

      {/* Job rows */}
      {jobs.map((jobWrapper, index) => {
        const job = jobWrapper.job;
        const jobId = job.id;
        const fileName = jobWrapper.gcodeFile?.name || jobWrapper.gcodeFile?.fileName || job.name || "Unknown File";
        const printerName = jobWrapper.assignedPrinter?.name || "Unknown Printer";
        const model = jobWrapper.assignedPrinter?.modelName || "";
        const material = jobWrapper.gcodeFile?.materialType || job.requiredMaterialType || "";
        const status = job.status || "Unknown";
        const priority = job.priority || 0;
        const projectName = job.projectName;

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
        const thumbnailUrl = jobWrapper.gcodeFile?.thumbnailUrl;
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

        const isDragTarget = dropIndex === index && dragIndex !== index;
        const isLastJob = index === jobs.length - 1;

        return (
          <div
            key={jobId}
            role="listitem"
            aria-label={`Print job: ${fileName}${deadlineState === "overdue" ? ", overdue deadline" : deadlineState === "due-soon" ? ", due soon" : ""}`}
            draggable
            onDragStart={(e) => handleDragStart(e, index)}
            onDragEnd={handleDragEnd}
            onDragEnter={(e) => handleDragEnter(e, index)}
            onDragLeave={handleDragLeave}
            onDragOver={handleDragOver}
            onDrop={(e) => handleDrop(e, index)}
            className={clsx(
              "transition-colors cursor-pointer hover:bg-pf-bg-2/50",
              !isLastJob && "border-b border-pf-border",
              isDragTarget && "border-t-2 border-t-pf-accent",
              deadlineState === "due-soon" && "bg-pf-warning/5",
              deadlineState === "overdue" && "bg-pf-error/10",
            )}
            onClick={() => onEdit?.(jobId)}
            tabIndex={0}
            onKeyDown={(e) => {
              if (e.key === "Enter" || e.key === " ") {
                e.preventDefault();
                onEdit?.(jobId);
              }
            }}
          >
            {/* Row 1 — Primary: drag, thumbnail, file name, status, printer, copies, priority, actions */}
            <div className="grid grid-cols-[40px_56px_1fr_auto_auto_auto_auto_auto_auto] items-center gap-x-2 px-2 pt-2.5 pb-1 text-sm">
              {/* Drag handle */}
              <div
                className="flex items-center justify-center cursor-grab active:cursor-grabbing text-pf-text-tertiary hover:text-pf-text-secondary"
                onClick={(e) => e.stopPropagation()}
                aria-label="Drag to reorder"
              >
                <GripVertical className="h-4 w-4" aria-hidden="true" />
              </div>

              {/* Thumbnail */}
              <div className="flex items-center justify-center">
                {thumbnailUrl ? (
                  <img
                    src={thumbnailUrl}
                    alt=""
                    className="w-10 h-10 min-w-10 rounded object-cover bg-pf-bg-2"
                  />
                ) : (
                  <div className="w-10 h-10 min-w-10 rounded bg-pf-bg-2 flex items-center justify-center text-pf-text-tertiary text-xs">
                    —
                  </div>
                )}
              </div>

              {/* File name */}
              <div className="px-2 min-w-0">
                <div className="font-medium text-pf-text-primary truncate" title={fileName}>
                  {fileName}
                </div>
              </div>

              {/* Status badge */}
              <div className="px-2 w-20 flex justify-center">
                <span
                  className={clsx(
                    "inline-block px-2 py-0.5 rounded-full text-xs font-medium whitespace-nowrap",
                    getStatusColor(status),
                  )}
                >
                  {status}
                </span>
              </div>

              {/* Printer */}
              <div className="px-2 w-32 text-pf-text-secondary truncate" title={printerName}>
                {printerName}
              </div>

              {/* Copies */}
              <div className="px-2 w-16 text-center whitespace-nowrap">
                {(job.copies ?? 1) > 1 ? (
                  <span className="text-pf-text-primary font-medium text-xs">
                    {job.completedCopies ?? 0}/{job.copies}
                  </span>
                ) : (
                  <span className="text-pf-text-tertiary text-xs">1</span>
                )}
              </div>

              {/* Priority */}
              <div className="px-2 w-28" onClick={(e) => e.stopPropagation()}>
                <Select
                  value={priority}
                  onChange={(e) => onPriority?.(jobId, parseInt(e.target.value))}
                  className="text-xs w-full"
                  aria-label="Job priority"
                >
                  <option value="0">Normal</option>
                  <option value="1">High</option>
                  <option value="2">Urgent</option>
                  <option value="-1">Low</option>
                </Select>
              </div>

              {/* Deadline */}
              <div className="px-2 w-44">
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
                      <span className="inline-flex items-center rounded-full bg-pf-error/15 px-1.5 py-0.5 text-[10px] font-semibold text-pf-error">
                        Overdue
                      </span>
                    )}
                    {deadlineState === "due-soon" && (
                      <span className="inline-flex items-center rounded-full bg-pf-warning/20 px-1.5 py-0.5 text-[10px] font-semibold text-pf-warning">
                        Due soon
                      </span>
                    )}
                  </div>
                )}
              </div>

              {/* Actions */}
              <div className="px-2 min-w-[180px]" onClick={(e) => e.stopPropagation()}>
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
              </div>
            </div>

            {/* Row 2 — Secondary: detail chips with supplementary info */}
            <div className="flex items-center gap-x-4 gap-y-1 flex-wrap pl-[104px] pr-4 pb-2.5 pt-0.5">
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
          </div>
        );
      })}
    </div>
  );
}
