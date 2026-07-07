import { useCallback } from "react";
import { Button } from "@/common/components/ui/Button";
import type { HistoryJob } from "@/types/queue";

/** Collapse repeated/duplicated material tokens (e.g. "PETG;PETG;PETG") to a compact list. */
function formatMaterial(material?: string | null): string | null {
  if (!material) return null;
  const parts = material
    .split(/[;,]/)
    .map((s) => s.trim())
    .filter(Boolean);
  if (parts.length === 0) return material.trim() || null;
  return [...new Set(parts)].join(", ");
}

interface HistoryJobTableProps {
  jobs: HistoryJob[];
  onRerun: (jobId: string) => void;
  onViewDetails?: (jobId: string) => void;
}

/**
 * HistoryJobTable Component
 * 
 * Displays job history in a compact table format with sortable columns.
 * Shows: Job Name, Printer, Status, Duration, Completed, Actions
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

  const getStatusBadge = useCallback((status: string, completionPercentage?: number) => {
    const baseClasses = "px-2 py-0.5 rounded-sm text-xs font-medium whitespace-nowrap";
    const showProgress =
      (status === "failed" || status === "cancelled") &&
      typeof completionPercentage === "number" &&
      completionPercentage > 0 &&
      completionPercentage < 100;
    const progressSuffix = showProgress ? ` @ ${Math.round(completionPercentage!)}%` : "";
    switch (status) {
      case "completed":
        return <span className={`${baseClasses} bg-pf-success-bg text-pf-success border border-pf-success`}>✓ Completed</span>;
      case "failed":
        return <span className={`${baseClasses} bg-pf-error-bg text-pf-error border border-pf-error`}>✗ Failed{progressSuffix}</span>;
      case "cancelled":
        return <span className={`${baseClasses} bg-pf-warning-bg text-pf-warning border border-pf-warning`}>◯ Cancelled{progressSuffix}</span>;
      default:
        return <span className={`${baseClasses} bg-pf-bg-0 text-pf-text-secondary border border-pf-border`}>{status}</span>;
    }
  }, []);

  return (
    <div className="bg-pf-bg-0 border border-pf-border rounded-lg overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full table-fixed text-sm">
          <colgroup>
            <col />
            <col className="w-28" />
            <col className="w-24" />
            <col className="w-32" />
            <col className="w-24" />
            <col className="w-24" />
            <col className="w-24" />
            <col className="w-28" />
            <col className="w-20" />
          </colgroup>
          <thead>
            <tr className="bg-pf-bg-1 border-b border-pf-border">
              <th className="text-left px-4 py-3 font-medium text-pf-text-secondary">Job Name</th>
              <th className="text-left px-4 py-3 font-medium text-pf-text-secondary">Printer</th>
              <th className="text-left px-4 py-3 font-medium text-pf-text-secondary">Material</th>
              <th className="text-left px-4 py-3 font-medium text-pf-text-secondary">Status</th>
              <th className="text-right px-4 py-3 font-medium text-pf-text-secondary">Filament</th>
              <th className="text-right px-4 py-3 font-medium text-pf-text-secondary">Cost</th>
              <th className="text-right px-4 py-3 font-medium text-pf-text-secondary">Duration</th>
              <th className="text-right px-4 py-3 font-medium text-pf-text-secondary">Completed</th>
              <th className="text-center px-4 py-3 font-medium text-pf-text-secondary">Actions</th>
            </tr>
          </thead>
          <tbody>
            {jobs.map((job, index) => {
              const material = formatMaterial(job.materialType);
              return (
                <tr
                  key={job.id}
                  className={`border-b border-pf-border hover:bg-pf-bg-1 transition-colors ${
                    index % 2 === 0 ? "bg-pf-bg-0" : "bg-pf-bg-0/50"
                  }`}
                >
                  {/* Job Name */}
                  <td className="px-4 py-3">
                    <div className="font-medium text-pf-text-primary truncate" title={job.name}>
                      {job.name}
                    </div>
                    {job.status === "failed" && job.failureReason && (
                      <div className="text-xs text-pf-error truncate" title={job.failureReason}>
                        {job.failureReason}
                      </div>
                    )}
                  </td>

                  {/* Printer */}
                  <td className="px-4 py-3 text-pf-text-secondary">
                    <div className="truncate" title={job.printerName}>
                      {job.printerName}
                    </div>
                  </td>

                  {/* Material */}
                  <td className="px-4 py-3 text-pf-text-secondary">
                    {material ? (
                      <span className="truncate block" title={job.materialType ?? undefined}>
                        {material}
                      </span>
                    ) : (
                      <span className="text-pf-text-muted">—</span>
                    )}
                  </td>

                  {/* Status */}
                  <td className="px-4 py-3">
                    {getStatusBadge(job.status, job.completionPercentage)}
                  </td>

                  {/* Filament Usage */}
                  <td className="px-4 py-3 text-right text-pf-text-primary tabular-nums">
                    {job.toolheadUsages && job.toolheadUsages.length > 0 ? (
                      (() => {
                        const totalGrams = job.toolheadUsages.reduce(
                          (sum, u) => sum + (u.filamentUsageGrams ?? 0),
                          0
                        );
                        return (
                          <span title={job.toolheadUsages.map(u => `T${u.toolheadIndex}: ${u.filamentUsageGrams?.toFixed(1) ?? '—'}g (${u.filamentName || 'Unknown'})`).join('\n')}>
                            {totalGrams.toFixed(1)}g
                          </span>
                        );
                      })()
                    ) : job.actualFilamentUsageGrams != null && job.actualFilamentUsageGrams > 0 ? (
                      <span title="Actual filament reported by the printer">
                        {job.actualFilamentUsageGrams.toFixed(1)}g
                      </span>
                    ) : job.estimatedFilamentUsageGrams != null && job.estimatedFilamentUsageGrams > 0 ? (
                      <span className="inline-flex items-baseline justify-end gap-1" title="Slicer estimate (no actual usage reported)">
                        <span>{job.estimatedFilamentUsageGrams.toFixed(1)}g</span>
                        <span className="text-[10px] uppercase tracking-wide text-pf-text-muted">est</span>
                      </span>
                    ) : (
                      <span className="text-pf-text-muted">—</span>
                    )}
                  </td>

                  {/* Cost */}
                  <td className="px-4 py-3 text-right text-pf-text-primary tabular-nums">
                    {(() => {
                      const hasUsages = !!(job.toolheadUsages && job.toolheadUsages.length > 0);
                      const cost = hasUsages
                        ? job.toolheadUsages!.reduce((sum, u) => sum + (u.materialCostUsd ?? 0), 0)
                        : job.materialCostUsd ?? 0;

                      if (!(cost > 0)) {
                        return <span className="text-pf-text-muted">—</span>;
                      }

                      const estimated = job.costIsEstimated === true;
                      const lines: string[] = [];
                      if (hasUsages) {
                        for (const u of job.toolheadUsages!) {
                          lines.push(`T${u.toolheadIndex}: $${u.materialCostUsd?.toFixed(2) ?? '—'}${u.filamentName ? ` (${u.filamentName})` : ''}`);
                        }
                      } else {
                        lines.push(estimated ? "Estimated material cost" : "Material cost");
                      }
                      if (job.totalCostUsd != null && Math.abs(job.totalCostUsd - cost) > 0.005) {
                        lines.push(`Total incl. energy/labor: $${job.totalCostUsd.toFixed(2)}`);
                      }

                      return (
                        <span className="inline-flex items-baseline justify-end gap-1" title={lines.join("\n")}>
                          <span>${cost.toFixed(2)}</span>
                          {estimated && (
                            <span className="text-[10px] uppercase tracking-wide text-pf-text-muted">est</span>
                          )}
                        </span>
                      );
                    })()}
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
                      {(job.status === "completed" || job.status === "cancelled") && (
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
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
