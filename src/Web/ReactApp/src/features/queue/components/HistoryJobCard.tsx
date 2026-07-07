import { Button } from "@/common/components/ui/Button";
import { Badge } from "@/common/components/ui/Badge";
import type { HistoryJob } from "@/types/queue";

function getCategoryBadgeVariant(category?: string | null): "default" | "primary" | "success" | "warning" | "error" | "info" {
  switch (category) {
    case "material": return "primary";
    case "color": return "info";
    case "nozzle": return "warning";
    default: return "default";
  }
}

function getCategoryIcon(category: string): string {
  switch (category) {
    case "material": return "🧵";
    case "color": return "🎨";
    case "nozzle": return "⊘";
    default: return "🏷";
  }
}

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

      {/* Tags */}
      {job.tags && job.tags.length > 0 && (
        <div className="flex flex-wrap gap-1.5 mb-3">
          {job.tags.map((tag) => (
            <Badge
              key={tag.id}
              variant={getCategoryBadgeVariant(tag.category)}
              size="sm"
            >
              {tag.category && (
                <span className="opacity-60 mr-0.5">{getCategoryIcon(tag.category)}</span>
              )}
              {tag.name}
            </Badge>
          ))}
        </div>
      )}

      {/* Failure Reason (if failed) */}
      {job.status === "failed" && job.failureReason && (
        <div className="mb-4 p-3 bg-pf-error-bg border border-pf-error rounded-sm text-sm text-pf-text-primary">
          <div className="font-medium text-pf-error mb-1">Failure Reason:</div>
          <div className="text-pf-text-secondary">{job.failureReason}</div>
        </div>
      )}

      {/* Per-Toolhead Filament Usage and Cost */}
      {job.toolheadUsages && job.toolheadUsages.length > 0 && (
        <div className="mb-4">
          <div className="text-xs text-pf-text-secondary mb-2">Filament Usage</div>
          <div className="space-y-1.5">
            {job.toolheadUsages.map((usage, idx) => {
              const usageGrams = usage.filamentUsageGrams?.toFixed(1) ?? '—';
              const costUsd = usage.materialCostUsd?.toFixed(2) ?? '—';
              return (
                <div key={usage.id || idx} className="flex items-center gap-2 text-xs">
                  <span className="font-mono text-pf-text-primary w-6">T{usage.toolheadIndex}</span>
                  <div className="flex items-center gap-1.5 flex-1 min-w-0">
                    {usage.filamentColor && (
                      <span
                        className="w-3 h-3 rounded-full border border-pf-border shrink-0"
                        style={{ backgroundColor: usage.filamentColor }}
                        title={usage.filamentColor}
                      />
                    )}
                    <span className="text-pf-text-primary truncate">
                      {usage.filamentName || 'Unknown'}
                    </span>
                  </div>
                  <div className="flex items-center gap-2 text-pf-text-secondary shrink-0">
                    <span className="font-medium tabular-nums">{usageGrams}g</span>
                    {usage.materialCostUsd != null && (
                      <span className="text-pf-text-tertiary tabular-nums">${costUsd}</span>
                    )}
                  </div>
                </div>
              );
            })}
            {/* Total Row */}
            {job.toolheadUsages.length > 1 && (() => {
              const totalGrams = job.toolheadUsages.reduce(
                (sum, u) => sum + (u.filamentUsageGrams ?? 0),
                0
              );
              const totalCost = job.toolheadUsages.reduce(
                (sum, u) => sum + (u.materialCostUsd ?? 0),
                0
              );
              return (
                <div className="flex items-center gap-2 text-xs pt-1.5 border-t border-pf-border/50">
                  <span className="font-medium text-pf-text-primary w-6">Total</span>
                  <div className="flex-1" />
                  <div className="flex items-center gap-2 shrink-0">
                    <span className="font-medium text-pf-text-primary tabular-nums">{totalGrams.toFixed(1)}g</span>
                    {totalCost > 0 && (
                      <span className="font-medium text-pf-text-primary tabular-nums">${totalCost.toFixed(2)}</span>
                    )}
                  </div>
                </div>
              );
            })()}
          </div>
        </div>
      )}

      {/* Aggregate Filament Usage / Cost fallback (no per-toolhead usage records) */}
      {(!job.toolheadUsages || job.toolheadUsages.length === 0) &&
        ((job.actualFilamentUsageGrams != null && job.actualFilamentUsageGrams > 0) ||
          (job.materialCostUsd != null && job.materialCostUsd > 0)) && (
          <div className="mb-4">
            <div className="text-xs text-pf-text-secondary mb-2">Filament Usage</div>
            <div className="flex items-center gap-2 text-xs">
              <span className="text-pf-text-primary flex-1 min-w-0 truncate">
                Actual usage
              </span>
              <div className="flex items-center gap-2 text-pf-text-secondary shrink-0">
                {job.actualFilamentUsageGrams != null && job.actualFilamentUsageGrams > 0 && (
                  <span className="font-medium tabular-nums">
                    {job.actualFilamentUsageGrams.toFixed(1)}g
                  </span>
                )}
                {job.materialCostUsd != null && job.materialCostUsd > 0 && (
                  <span
                    className={`tabular-nums ${job.costIsEstimated ? 'text-pf-text-secondary italic' : 'text-pf-text-tertiary'}`}
                    title={
                      job.costIsEstimated
                        ? `Estimated from filament used (no spool associated).${job.totalCostUsd != null ? ` Total job cost: $${job.totalCostUsd.toFixed(2)}` : ''}`
                        : job.totalCostUsd != null
                        ? `Material cost. Total job cost: $${job.totalCostUsd.toFixed(2)}`
                        : 'Material cost'
                    }
                  >
                    {job.costIsEstimated ? '~' : ''}${job.materialCostUsd.toFixed(2)}
                    {job.costIsEstimated ? ' (est.)' : ''}
                  </span>
                )}
              </div>
            </div>
          </div>
        )}

      {/* Actions */}
      <div className="flex gap-2 pt-3 border-t border-pf-border">
        {(job.status === "completed" || job.status === "cancelled") && (
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
