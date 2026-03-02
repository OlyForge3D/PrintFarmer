import { Button, Select, Spinner } from "@/common/components/ui";
import { useState, useRef, useCallback } from "react";
import { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";
import type { DispatchUploadProgressDto } from "@/types/api";
import { Download, GripVertical, Tractor } from "lucide-react";

/**
 * Formats a duration in seconds to a human-readable string (e.g., "2h 30m" or "45m")
 */
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
  onReorder,
}: QueueJobsTableProps) {
  const [dragIndex, setDragIndex] = useState<number | null>(null);
  const [dropIndex, setDropIndex] = useState<number | null>(null);
  const dragCounter = useRef(0);

  const handleDragStart = useCallback((e: React.DragEvent<HTMLTableRowElement>, index: number) => {
    setDragIndex(index);
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/plain", String(index));
    // Make the dragged row semi-transparent
    requestAnimationFrame(() => {
      (e.target as HTMLElement).style.opacity = "0.4";
    });
  }, []);

  const handleDragEnd = useCallback((e: React.DragEvent<HTMLTableRowElement>) => {
    (e.target as HTMLElement).style.opacity = "1";
    setDragIndex(null);
    setDropIndex(null);
    dragCounter.current = 0;
  }, []);

  const handleDragEnter = useCallback((e: React.DragEvent<HTMLTableRowElement>, index: number) => {
    e.preventDefault();
    dragCounter.current++;
    setDropIndex(index);
  }, []);

  const handleDragLeave = useCallback((e: React.DragEvent<HTMLTableRowElement>) => {
    e.preventDefault();
    dragCounter.current--;
    if (dragCounter.current === 0) {
      setDropIndex(null);
    }
  }, []);

  const handleDragOver = useCallback((e: React.DragEvent<HTMLTableRowElement>) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
  }, []);

  const handleDrop = useCallback((e: React.DragEvent<HTMLTableRowElement>, targetIndex: number) => {
    e.preventDefault();
    dragCounter.current = 0;
    setDropIndex(null);

    if (dragIndex === null || dragIndex === targetIndex) {
      setDragIndex(null);
      return;
    }

    // Build the reordered list and compute new positions
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
            href="/files?tab=gcode"
            className="mt-4 px-4 py-2 bg-pf-accent hover:bg-pf-accent-dark text-white rounded-lg font-medium transition-colors"
          >
            Browse G-Code Files
          </a>
        </div>
      </div>
    );
  }

  return (
    <div className="overflow-x-auto border border-pf-border rounded-lg bg-pf-bg-1">
      <table className="w-full text-sm whitespace-nowrap">
        <thead>
          <tr className="border-b border-pf-border bg-pf-bg-2">
            <th className="w-10 px-2 py-3" aria-label="Reorder">
              <span className="sr-only">Drag to reorder</span>
            </th>
            <th className="w-14 min-w-14 px-2 py-3" aria-label="Thumbnail">
              <span className="sr-only">Thumbnail</span>
            </th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Time</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">File</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Project</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Printer</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Model</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Material</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Filament</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Est. Time</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Cost</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Copies</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Source</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Status</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Priority</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary whitespace-nowrap">Actions</th>
          </tr>
        </thead>
        <tbody>
          {jobs.map((jobWrapper, index) => {
            const job = jobWrapper.job;
            const jobId = job.id;
            const fileName = jobWrapper.gcodeFile?.name || jobWrapper.gcodeFile?.fileName || job.name || "Unknown File";
            const printerName = jobWrapper.assignedPrinter?.name || "Unknown Printer";
            const model = jobWrapper.assignedPrinter?.modelName || "Unknown Model";
            const material = jobWrapper.gcodeFile?.materialType || job.requiredMaterialType || "-";
            const status = job.status || "Unknown";
            const priority = job.priority || 0;
            const projectName = job.projectName;
            
            // Get estimated time from job or gcode file metadata
            const estimatedTimeSeconds = job.estimatedPrintTimeSeconds || jobWrapper.gcodeFile?.estimatedPrintTimeSeconds;
            const estimatedTimeDisplay = estimatedTimeSeconds 
              ? formatDuration(estimatedTimeSeconds)
              : "-";
            
            // Get filament usage from job or gcode file metadata (convert grams to display)
            const filamentGrams = job.estimatedFilamentUsageGrams || jobWrapper.gcodeFile?.estimatedFilamentUsageGrams;
            const filamentDisplay = job.filamentName
              ? (
                <span className="inline-flex items-center gap-1.5" title={`${job.filamentVendor ? job.filamentVendor + ' — ' : ''}${job.filamentName}`}>
                  {job.filamentColor && (
                    <span
                      className="inline-block w-3 h-3 rounded-full border border-pf-border shrink-0"
                      style={{ backgroundColor: job.filamentColor }}
                      aria-hidden="true"
                    />
                  )}
                  <span>{job.filamentName}</span>
                </span>
              )
              : filamentGrams 
                ? `${filamentGrams.toFixed(1)}g`
                : "-";

            const timeDisplay = formatDateTime(job.actualStartTimeUtc ?? job.queuedAtUtc);
            const thumbnailUrl = jobWrapper.gcodeFile?.thumbnailUrl;
            const estimatedCost = job.estimatedCost;
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

            return (
              <tr
                key={jobId}
                draggable
                onDragStart={(e) => handleDragStart(e, index)}
                onDragEnd={handleDragEnd}
                onDragEnter={(e) => handleDragEnter(e, index)}
                onDragLeave={handleDragLeave}
                onDragOver={handleDragOver}
                onDrop={(e) => handleDrop(e, index)}
                className={`border-b border-pf-border hover:bg-pf-bg-2 transition-colors cursor-pointer ${
                  isDragTarget ? "border-t-2 border-t-pf-accent" : ""
                }`}
                onClick={() => onEdit?.(jobId)}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    onEdit?.(jobId);
                  }
                }}
              >
                <td
                  className="w-10 px-2 py-3 cursor-grab active:cursor-grabbing text-pf-text-tertiary hover:text-pf-text-secondary"
                  onClick={(e) => e.stopPropagation()}
                  aria-label="Drag to reorder"
                >
                  <GripVertical className="h-4 w-4" aria-hidden="true" />
                </td>
                <td className="w-14 min-w-14 px-2 py-3">
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
                </td>
                <td className="px-4 py-3 text-pf-text-secondary whitespace-nowrap">{timeDisplay}</td>
                <td className="px-4 py-3 whitespace-nowrap">
                  <div className="font-medium text-pf-text-primary">{fileName}</div>
                </td>
                <td className="px-4 py-3 whitespace-nowrap">
                  {projectName ? (
                    <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-pf-accent/10 text-pf-accent border border-pf-accent/20">
                      {projectName}
                    </span>
                  ) : (
                    <span className="text-pf-text-tertiary">-</span>
                  )}
                </td>
                <td className="px-4 py-3 text-pf-text-secondary whitespace-nowrap">{printerName}</td>
                <td className="px-4 py-3 text-pf-text-secondary whitespace-nowrap">{model}</td>
                <td className="px-4 py-3 text-pf-text-secondary whitespace-nowrap">{material}</td>
                <td className="px-4 py-3 text-pf-text-secondary whitespace-nowrap">{filamentDisplay}</td>
                <td className="px-4 py-3 text-pf-text-secondary whitespace-nowrap">{estimatedTimeDisplay}</td>
                <td className="px-4 py-3 text-pf-text-secondary whitespace-nowrap">
                  {estimatedCost != null ? `$${estimatedCost.toFixed(2)}` : "-"}
                </td>
                <td className="px-4 py-3 whitespace-nowrap">
                  {(job.copies ?? 1) > 1 ? (
                    <span className="text-pf-text-primary font-medium">
                      {job.completedCopies ?? 0} / {job.copies}
                    </span>
                  ) : (
                    <span className="text-pf-text-tertiary">1</span>
                  )}
                </td>
                <td className="px-4 py-3 whitespace-nowrap">
                  {job.wasSeededFromHistory ? (
                    <span
                      role="img"
                      aria-label="Imported"
                      title="Imported"
                      className="inline-flex items-center justify-center w-7 h-6 rounded text-xs font-medium bg-pf-bg-2 text-pf-text-secondary border border-pf-border"
                    >
                      <Download aria-hidden="true" className="h-4 w-4" />
                    </span>
                  ) : (
                    <span
                      role="img"
                      aria-label="PrintFarmer"
                      title="PrintFarmer"
                      className="inline-flex items-center justify-center w-7 h-6 rounded text-xs font-medium bg-pf-bg-1 text-pf-text-tertiary border border-pf-border"
                    >
                      <Tractor aria-hidden="true" className="h-4 w-4" />
                    </span>
                  )}
                </td>
                <td className="px-4 py-3 whitespace-nowrap">
                  <span
                    className={`inline-block px-2 py-1 rounded-full text-xs font-medium ${getStatusColor(
                      status
                    )}`}
                  >
                    {status}
                  </span>
                </td>
                <td className="px-4 py-3 whitespace-nowrap" onClick={(e) => e.stopPropagation()}>
                  <Select
                    value={priority}
                    onChange={(e) =>
                      onPriority?.(jobId, parseInt(e.target.value))
                    }
                    className="text-xs w-28"
                  >
                    <option value="0">Normal</option>
                    <option value="1">High</option>
                    <option value="2">Urgent</option>
                    <option value="-1">Low</option>
                  </Select>
                </td>
                <td className="px-4 py-3 whitespace-nowrap" onClick={(e) => e.stopPropagation()}>
                  <div className="flex gap-2">
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
                            <Spinner className="h-4 w-4" />
                            {dispatchProgressText}
                          </span>
                        ) : (
                          "Start Print"
                        )}
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
                        Abort Print
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
                        Cancel Job
                      </Button>
                    )}
                  </div>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
