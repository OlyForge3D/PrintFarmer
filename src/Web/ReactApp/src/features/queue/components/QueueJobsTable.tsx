import { Button } from "@/common/components/ui/Button";
import { useState } from "react";
import { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";

export interface QueueJobsTableProps {
  jobs: QueuedPrintJobWithFileMetaDto[];
  isLoading?: boolean;
  onPause?: (jobId: string) => void;
  onResume?: (jobId: string) => void;
  onCancel?: (jobId: string) => void;
  onPriority?: (jobId: string, priority: number) => void;
}

export function QueueJobsTable({
  jobs,
  isLoading = false,
  onPause,
  onResume,
  onCancel,
  onPriority,
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
        return "bg-blue-100 text-blue-800";
      case "Printing":
        return "bg-green-100 text-green-800";
      case "Paused":
        return "bg-yellow-100 text-yellow-800";
      case "Completed":
        return "bg-gray-100 text-gray-800";
      case "Failed":
        return "bg-red-100 text-red-800";
      case "Cancelled":
        return "bg-gray-100 text-gray-800";
      default:
        return "bg-gray-100 text-gray-800";
    }
  };

  if (isLoading) {
    return (
      <div className="flex justify-center items-center py-8">
        <div className="text-gray-600">Loading jobs...</div>
      </div>
    );
  }

  if (jobs.length === 0) {
    return (
      <div className="flex justify-center items-center py-8">
        <div className="text-gray-600">No jobs in queue</div>
      </div>
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-gray-200 bg-gray-50">
            <th className="px-4 py-3 text-left">
              <input
                type="checkbox"
                checked={selectedJobs.size === jobs.length && jobs.length > 0}
                onChange={handleSelectAll}
                className="rounded border-gray-300"
              />
            </th>
            <th className="px-4 py-3 text-left font-medium">File</th>
            <th className="px-4 py-3 text-left font-medium">Printer</th>
            <th className="px-4 py-3 text-left font-medium">Model</th>
            <th className="px-4 py-3 text-left font-medium">Material</th>
            <th className="px-4 py-3 text-left font-medium">Status</th>
            <th className="px-4 py-3 text-left font-medium">Priority</th>
            <th className="px-4 py-3 text-left font-medium">Actions</th>
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
                className="border-b border-gray-200 hover:bg-gray-50"
              >
                <td className="px-4 py-3">
                  <input
                    type="checkbox"
                    checked={selectedJobs.has(jobWrapper.id)}
                    onChange={() => handleSelectJob(jobWrapper.id)}
                    className="rounded border-gray-300"
                  />
                </td>
                <td className="px-4 py-3">
                  <div className="font-medium text-gray-900">{fileName}</div>
                </td>
                <td className="px-4 py-3 text-gray-600">{printerName}</td>
                <td className="px-4 py-3 text-gray-600">{model}</td>
                <td className="px-4 py-3 text-gray-600">{material}</td>
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
                  <select
                    value={priority}
                    onChange={(e) =>
                      onPriority?.(jobWrapper.id, parseInt(e.target.value))
                    }
                    className="px-2 py-1 border border-gray-300 rounded-md text-xs focus:outline-none focus:ring-blue-500 focus:border-blue-500"
                  >
                    <option value="0">Normal</option>
                    <option value="1">High</option>
                    <option value="2">Urgent</option>
                    <option value="-1">Low</option>
                  </select>
                </td>
                <td className="px-4 py-3">
                  <div className="flex gap-2">
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
