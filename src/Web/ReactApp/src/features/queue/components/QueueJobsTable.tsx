import { Button, Checkbox, Select } from "@/common/components/ui";
import { useState } from "react";
import { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";

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

export interface QueueJobsTableProps {
  jobs: QueuedPrintJobWithFileMetaDto[];
  isLoading?: boolean;
  dispatchingJobId?: string | null;
  onPause?: (jobId: string) => void;
  onResume?: (jobId: string) => void;
  onCancel?: (jobId: string) => void;
  onPriority?: (jobId: string, priority: number) => void;
  onEdit?: (jobId: string) => void;
  onDispatch?: (jobId: string) => void;
}

export function QueueJobsTable({
  jobs,
  isLoading = false,
  dispatchingJobId = null,
  onPause,
  onResume,
  onCancel,
  onPriority,
  onEdit,
  onDispatch,
}: QueueJobsTableProps) {
  const [selectedJobs, setSelectedJobs] = useState<Set<string>>(new Set());

  const handleSelectJob = (jobId: string) => {
    const newSelected = new Set(selectedJobs);
    if (newSelected.has(jobId)) {
      newSelected.delete(jobId);
    } else {
      newSelected.add(jobId);
    }
    setSelectedJobs(newSelected);
  };

  const handleSelectAll = () => {
    if (selectedJobs.size === jobs.length) {
      setSelectedJobs(new Set());
    } else {
      setSelectedJobs(new Set(jobs.map((job) => job.job.id)));
    }
  };

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
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-pf-border bg-pf-bg-2">
            <th className="px-4 py-3 text-left">
              <Checkbox
                checked={selectedJobs.size === jobs.length && jobs.length > 0}
                onChange={handleSelectAll}
              />
            </th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">File</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">Printer</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">Model</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">Material</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">Est. Time</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">Filament</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">Status</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">Priority</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">Actions</th>
          </tr>
        </thead>
        <tbody>
          {jobs.map((jobWrapper) => {
            const job = jobWrapper.job;
            const jobId = job.id;
            const fileName = jobWrapper.gcodeFile?.name || jobWrapper.gcodeFile?.fileName || job.name || "Unknown File";
            const printerName = jobWrapper.assignedPrinter?.name || "Unknown Printer";
            const model = jobWrapper.assignedPrinter?.modelName || "Unknown Model";
            const material = jobWrapper.gcodeFile?.materialType || job.requiredMaterialType || "-";
            const status = job.status || "Unknown";
            const priority = job.priority || 0;
            
            // Get estimated time from job or gcode file metadata
            const estimatedTimeSeconds = job.estimatedPrintTimeSeconds || jobWrapper.gcodeFile?.estimatedPrintTimeSeconds;
            const estimatedTimeDisplay = estimatedTimeSeconds 
              ? formatDuration(estimatedTimeSeconds)
              : "-";
            
            // Get filament usage from job or gcode file metadata (convert grams to display)
            const filamentGrams = job.estimatedFilamentUsageGrams || jobWrapper.gcodeFile?.estimatedFilamentUsageGrams;
            const filamentDisplay = filamentGrams 
              ? `${filamentGrams.toFixed(1)}g`
              : "-";

            return (
              <tr
                key={jobId}
                className="border-b border-pf-border hover:bg-pf-bg-2 transition-colors cursor-pointer"
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
                <td className="px-4 py-3" onClick={(e) => e.stopPropagation()}>
                  <Checkbox
                    checked={selectedJobs.has(jobId)}
                    onChange={() => handleSelectJob(jobId)}
                  />
                </td>
                <td className="px-4 py-3">
                  <div className="font-medium text-pf-text-primary">{fileName}</div>
                </td>
                <td className="px-4 py-3 text-pf-text-secondary">{printerName}</td>
                <td className="px-4 py-3 text-pf-text-secondary">{model}</td>
                <td className="px-4 py-3 text-pf-text-secondary">{material}</td>
                <td className="px-4 py-3 text-pf-text-secondary">{estimatedTimeDisplay}</td>
                <td className="px-4 py-3 text-pf-text-secondary">{filamentDisplay}</td>
                <td className="px-4 py-3">
                  <span
                    className={`inline-block px-2 py-1 rounded-full text-xs font-medium ${getStatusColor(
                      status
                    )}`}
                  >
                    {status}
                  </span>
                </td>
                <td className="px-4 py-3" onClick={(e) => e.stopPropagation()}>
                  <Select
                    value={priority}
                    onChange={(e) =>
                      onPriority?.(jobId, parseInt(e.target.value))
                    }
                    className="text-xs w-24"
                  >
                    <option value="0">Normal</option>
                    <option value="1">High</option>
                    <option value="2">Urgent</option>
                    <option value="-1">Low</option>
                  </Select>
                </td>
                <td className="px-4 py-3" onClick={(e) => e.stopPropagation()}>
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
                            <svg className="animate-spin h-4 w-4" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                            </svg>
                            Starting...
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
                    {status !== "Completed" && (
                      <Button
                        onClick={(e) => {
                          e.stopPropagation();
                          onCancel?.(jobId);
                        }}
                        variant="danger"
                        size="sm"
                      >
                        Cancel
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
