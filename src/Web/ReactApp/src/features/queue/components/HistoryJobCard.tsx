import { Button } from "@/common/components/ui/Button";
import { HistoryJob } from "./QueueHistoryTab";

interface HistoryJobCardProps {
  job: HistoryJob;
  onRerun: () => void;
  onViewDetails?: (jobId: string) => void;
}

/**
 * HistoryJobCard Component
 *
 * Displays a completed, failed, or cancelled print job with:
 * - Job name and printer name
 * - Status badge (color-coded)
 * - Duration and completion details
 * - Failure reason (if failed)
 * - Rerun button for completed jobs
 * - View details button
 */
export default function HistoryJobCard({
  job,
  onRerun,
  onViewDetails,
}: HistoryJobCardProps) {
  const durationMinutes = Math.round(job.durationSeconds / 60);
  const durationHours = Math.floor(durationMinutes / 60);
  const durationMins = durationMinutes % 60;

  const formatDuration = () => {
    if (durationHours > 0) {
      return `${durationHours}h ${durationMins}m`;
    }
    return `${durationMins}m`;
  };

  const getStatusColor = () => {
    switch (job.status) {
      case "completed":
        return "bg-pf-success-bg text-pf-success border border-pf-success";
      case "failed":
        return "bg-pf-error-bg text-pf-error border border-pf-error";
      case "cancelled":
        return "bg-pf-warning-bg text-pf-warning border border-pf-warning";
      default:
        return "bg-pf-bg-0 text-pf-text-secondary border border-pf-border";
    }
  };

  const getStatusLabel = () => {
    switch (job.status) {
      case "completed":
        return "✓ Completed";
      case "failed":
        return "✗ Failed";
      case "cancelled":
        return "◯ Cancelled";
      default:
        return job.status;
    }
  };

  const getCompletionTimeText = () => {
    if (!job.completedAt) return "In progress";
    
    const completedDate = new Date(job.completedAt);
    
    const now = new Date();
    const diffMs = now.getTime() - completedDate.getTime();
    const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));
    const diffHours = Math.floor(diffMs / (1000 * 60 * 60));
    const diffMins = Math.floor(diffMs / (1000 * 60));
    
    if (diffDays > 0) return `${diffDays} day${diffDays > 1 ? "s" : ""} ago`;
    if (diffHours > 0) return `${diffHours} hour${diffHours > 1 ? "s" : ""} ago`;
    if (diffMins > 0) return `${diffMins} minute${diffMins > 1 ? "s" : ""} ago`;
    return "Just now";
  };

  return (
    <div className="bg-pf-bg-0 border border-pf-border rounded-lg p-4 hover:border-pf-border-hover transition-colors">
      {/* Header: Name and Status */}
      <div className="flex items-start justify-between gap-3 mb-3">
        <div className="flex-1">
          <h3 className="font-medium text-pf-text-primary truncate">{job.name}</h3>
          <p className="text-sm text-pf-text-secondary">{job.printerName}</p>
        </div>
        <span className={`px-2 py-1 rounded-sm text-xs font-medium whitespace-nowrap ${getStatusColor()}`}>
          {getStatusLabel()}
        </span>
      </div>

      {/* Details Grid */}
      <div className="grid grid-cols-2 gap-3 mb-4">
        <div>
          <div className="text-xs text-pf-text-secondary mb-1">Duration</div>
          <div className="text-sm font-medium text-pf-text-primary">
            {formatDuration()}
          </div>
        </div>
        <div>
          <div className="text-xs text-pf-text-secondary mb-1">Completed</div>
          <div className="text-sm font-medium text-pf-text-primary">
            {getCompletionTimeText()}
          </div>
        </div>
      </div>

      {/* Progress Bar */}
      <div className="mb-4">
        <div className="flex items-center justify-between mb-1">
          <span className="text-xs text-pf-text-secondary">Progress</span>
          <span className="text-xs font-medium text-pf-text-primary">
            {job.completionPercentage}%
          </span>
        </div>
        <div className="w-full bg-pf-bg-1 rounded-full h-2">
          <div
            className={`h-2 rounded-full transition-all ${
              job.status === "completed"
                ? "bg-pf-success"
                : job.status === "failed"
                ? "bg-pf-error"
                : "bg-pf-warning"
            }`}
            style={{ width: `${Math.min(100, job.completionPercentage)}%` }}
          />
        </div>
      </div>

      {/* Failure Reason (if failed) */}
      {job.status === "failed" && job.failureReason && (
        <div className="mb-4 p-3 bg-pf-error-bg border border-pf-error rounded-sm text-sm text-pf-text-primary">
          <div className="font-medium text-pf-error mb-1">Failure Reason:</div>
          <div className="text-pf-text-secondary">{job.failureReason}</div>
        </div>
      )}

      {/* Actions */}
      <div className="flex gap-2 pt-3 border-t border-pf-border">
        {job.status === "completed" && (
          <Button
            onClick={onRerun}
            className="flex-1 px-3 py-2 rounded-sm text-sm font-medium"
            variant="secondary"
          >
            ↻ Rerun
          </Button>
        )}
        {onViewDetails && (
          <Button
            onClick={() => onViewDetails(job.id)}
            className="flex-1 px-3 py-2 rounded-sm text-sm font-medium"
            variant="secondary"
          >
            View Details →
          </Button>
        )}
      </div>
    </div>
  );
}
