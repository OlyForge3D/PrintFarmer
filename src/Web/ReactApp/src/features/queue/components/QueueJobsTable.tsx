import { Button, Checkbox, Select } from "@/common/components/ui";
import { useState } from "react";
import { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";

export interface QueueJobsTableProps {
  jobs: QueuedPrintJobWithFileMetaDto[];
  isLoading?: boolean;
  onPause?: (jobId: string) => void;
  onResume?: (jobId: string) => void;
  onCancel?: (jobId: string) => void;
  onPriority?: (jobId: string, priority: number) => void;
  onEdit?: (jobId: string) => void;
}

export function QueueJobsTable({
  jobs,
  isLoading = false,
  onPause,
  onResume,
  onCancel,
  onPriority,
  onEdit,
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
      setSelectedJobs(new Set(jobs.map((job) => job.id)));
    }
  };

  const getStatusColor = (status: string) => {
    switch (status) {
      case "Queued":
        return "bg-pf-info-bg text-pf-info-text";
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
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">Status</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">Priority</th>
            <th className="px-4 py-3 text-left font-medium text-pf-text-primary">Actions</th>
          </tr>
        </thead>
        <tbody>
          {jobs.map((jobWrapper) => {
            const job = jobWrapper.job;
            const fileName = jobWrapper.fileMetadata?.fileName || "Unknown File";
            const printerName = jobWrapper.printerMetadata?.name || "Unknown Printer";
            const model = jobWrapper.printerMetadata?.modelName || "Unknown Model";
            const material = jobWrapper.fileMetadata?.materialType || "-";
            const status = job.status || "Unknown";
            const priority = job.priority || 0;

            return (
              <tr
                key={jobWrapper.id}
                className="border-b border-pf-border hover:bg-pf-bg-2 transition-colors"
              >
                <td className="px-4 py-3">
                  <Checkbox
                    checked={selectedJobs.has(jobWrapper.id)}
                    onChange={() => handleSelectJob(jobWrapper.id)}
                  />
                </td>
                <td className="px-4 py-3">
                  <div className="font-medium text-pf-text-primary">{fileName}</div>
                </td>
                <td className="px-4 py-3 text-pf-text-secondary">{printerName}</td>
                <td className="px-4 py-3 text-pf-text-secondary">{model}</td>
                <td className="px-4 py-3 text-pf-text-secondary">{material}</td>
                <td className="px-4 py-3">
                  <span
                    className={`inline-block px-2 py-1 rounded-full text-xs font-medium ${getStatusColor(
                      status
                    )}`}
                  >
                    {status}
                  </span>
                </td>
                <td className="px-4 py-3">
                  <Select
                    value={priority}
                    onChange={(e) =>
                      onPriority?.(jobWrapper.id, parseInt(e.target.value))
                    }
                    className="text-xs w-24"
                  >
                    <option value="0">Normal</option>
                    <option value="1">High</option>
                    <option value="2">Urgent</option>
                    <option value="-1">Low</option>
                  </Select>
                </td>
                <td className="px-4 py-3">
                  <div className="flex gap-2">
                    <Button
                      onClick={() => onEdit?.(jobWrapper.id)}
                      variant="subtle"
                      size="sm"
                    >
                      Edit
                    </Button>
                    {status === "Printing" && (
                      <Button
                        onClick={() => onPause?.(jobWrapper.id)}
                        variant="subtle"
                        size="sm"
                      >
                        Pause
                      </Button>
                    )}
                    {status === "Paused" && (
                      <Button
                        onClick={() => onResume?.(jobWrapper.id)}
                        variant="subtle"
                        size="sm"
                      >
                        Resume
                      </Button>
                    )}
                    {status !== "Completed" && (
                      <Button
                        onClick={() => onCancel?.(jobWrapper.id)}
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
