import { useCallback } from "react";
import { Button } from "@/common/components/ui/Button";
import type { HistoryJob } from "@/types/queue";

interface HistoryJobTableProps {
  jobs: HistoryJob[];
  onRerun: (jobId: string) => void;
  onViewDetails?: (jobId: string) => void;
}

/**
 * HistoryJobTable Component
 * 
 * Displays job history in a compact table format with sortable columns.
 * Shows: Job Name, Printer, Status, Progress, Duration, Completed, Actions
 */
export default function HistoryJobTable({
  jobs,
  onRerun,
  onViewDetails,
}: HistoryJobTableProps) {
  const formatDuration = useCallback((seconds: number) => {
    const minutes = Math.round(seconds / 60);
    const hours = Math.floor(minutes / 60);
    const mins = minutes % 60;
    if (hours > 0) {
      return `${hours}h ${mins}m`;
    }
    return `${mins}m`;
  }, []);

  const formatCompletedAt = useCallback((completedAt: string | null) => {
    if (!completedAt) return "—";
    
    const date = new Date(completedAt);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));
    const diffHours = Math.floor(diffMs / (1000 * 60 * 60));
    const diffMins = Math.floor(diffMs / (1000 * 60));
    
    if (diffDays > 7) {
      return date.toLocaleDateString();
    }
    if (diffDays > 0) return `${diffDays}d ago`;
    if (diffHours > 0) return `${diffHours}h ago`;
    if (diffMins > 0) return `${diffMins}m ago`;
    return "Just now";
  }, []);

  const getStatusBadge = useCallback((status: string) => {
    const baseClasses = "px-2 py-0.5 rounded-sm text-xs font-medium whitespace-nowrap";
    switch (status) {
      case "completed":
        return <span className={`${baseClasses} bg-pf-success-bg text-pf-success border border-pf-success`}>✓ Completed</span>;
      case "failed":
        return <span className={`${baseClasses} bg-pf-error-bg text-pf-error border border-pf-error`}>✗ Failed</span>;
      case "cancelled":
        return <span className={`${baseClasses} bg-pf-warning-bg text-pf-warning border border-pf-warning`}>◯ Cancelled</span>;
      default:
        return <span className={`${baseClasses} bg-pf-bg-0 text-pf-text-secondary border border-pf-border`}>{status}</span>;
    }
  }, []);

  return (
    <div className="bg-pf-bg-0 border border-pf-border rounded-lg overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="bg-pf-bg-1 border-b border-pf-border">
              <th className="text-left px-4 py-3 font-medium text-pf-text-secondary">Job Name</th>
              <th className="text-left px-4 py-3 font-medium text-pf-text-secondary">Printer</th>
              <th className="text-left px-4 py-3 font-medium text-pf-text-secondary">Status</th>
              <th className="text-center px-4 py-3 font-medium text-pf-text-secondary">Progress</th>
              <th className="text-right px-4 py-3 font-medium text-pf-text-secondary">Duration</th>
              <th className="text-right px-4 py-3 font-medium text-pf-text-secondary">Completed</th>
              <th className="text-center px-4 py-3 font-medium text-pf-text-secondary">Actions</th>
            </tr>
          </thead>
          <tbody>
            {jobs.map((job, index) => (
              <tr 
                key={job.id} 
                className={`border-b border-pf-border hover:bg-pf-bg-1 transition-colors ${
                  index % 2 === 0 ? "bg-pf-bg-0" : "bg-pf-bg-0/50"
                }`}
              >
                {/* Job Name */}
                <td className="px-4 py-3">
                  <div className="font-medium text-pf-text-primary truncate max-w-[200px]" title={job.name}>
                    {job.name}
                  </div>
                  {job.status === "failed" && job.failureReason && (
                    <div className="text-xs text-pf-error truncate max-w-[200px]" title={job.failureReason}>
                      {job.failureReason}
                    </div>
                  )}
                </td>
                
                {/* Printer */}
                <td className="px-4 py-3 text-pf-text-secondary truncate max-w-[150px]" title={job.printerName}>
                  {job.printerName}
                </td>
                
                {/* Status */}
                <td className="px-4 py-3">
                  {getStatusBadge(job.status)}
                </td>
                
                {/* Progress */}
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2 justify-center">
                    <div className="w-16 bg-pf-bg-2 rounded-full h-1.5">
                      <div
                        className={`h-1.5 rounded-full ${
                          job.status === "completed"
                            ? "bg-pf-success"
                            : job.status === "failed"
                            ? "bg-pf-error"
                            : "bg-pf-warning"
                        }`}
                        style={{ width: `${Math.min(100, job.completionPercentage)}%` }}
                      />
                    </div>
                    <span className="text-xs text-pf-text-secondary w-8 text-right">
                      {job.completionPercentage}%
                    </span>
                  </div>
                </td>
                
                {/* Duration */}
                <td className="px-4 py-3 text-right text-pf-text-primary tabular-nums">
                  {formatDuration(job.durationSeconds)}
                </td>
                
                {/* Completed */}
                <td className="px-4 py-3 text-right text-pf-text-secondary tabular-nums">
                  {formatCompletedAt(job.completedAt)}
                </td>
                
                {/* Actions */}
                <td className="px-4 py-3">
                  <div className="flex items-center justify-center gap-1">
                    {job.status === "completed" && (
                      <Button
                        onClick={() => onRerun(job.id)}
                        variant="ghost"
                        size="sm"
                        className="px-2 py-1 text-xs"
                        title="Rerun this job"
                      >
                        ↻
                      </Button>
                    )}
                    {onViewDetails && (
                      <Button
                        onClick={() => onViewDetails(job.id)}
                        variant="ghost"
                        size="sm"
                        className="px-2 py-1 text-xs"
                        title="View details"
                      >
                        →
                      </Button>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
