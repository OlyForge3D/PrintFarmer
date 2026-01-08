import { useState } from "react";
import { ModelStats, JobAction } from "./ModelFilteredJobsTab";
import { Button } from "@/common/components/ui/Button";

interface ModelJobsCardProps {
  model: ModelStats;
  isExpanded: boolean;
  onToggleExpand: () => void;
  onJobAction: (jobId: string, action: JobAction) => Promise<void>;
  onViewAllJobs: (modelName: string) => void;
}

/**
 * ModelJobsCard Component
 *
 * Displays a card for a single printer model with:
 * - Model name and status counts
 * - Expandable job list (first 3 jobs)
 * - Job action buttons (pause, resume, cancel)
 * - "View All Jobs" button to filter All Jobs tab by this model
 * - Average wait time indicator
 */
export default function ModelJobsCard({
  model,
  isExpanded,
  onToggleExpand,
  onJobAction,
  onViewAllJobs,
}: ModelJobsCardProps) {
  const [actingOnJobId, setActingOnJobId] = useState<string | null>(null);

  const handleJobAction = async (jobId: string, action: JobAction) => {
    try {
      setActingOnJobId(jobId);
      await onJobAction(jobId, action);
    } finally {
      setActingOnJobId(null);
    }
  };

  // Show first 3 jobs when expanded
  const displayedJobs = isExpanded ? model.jobs.slice(0, 3) : [];

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg overflow-hidden hover:shadow-md transition-shadow">
      {/* Header - Clickable to expand */}
      <div
        onClick={onToggleExpand}
        className="p-4 cursor-pointer hover:bg-pf-bg-0 transition-colors"
      >
        <div className="flex items-center justify-between">
          <div className="flex-1">
            <h3 className="font-semibold text-pf-text-primary text-lg">
              {model.name}
            </h3>
            <div className="flex gap-4 mt-2 flex-wrap">
              {model.queuedCount > 0 && (
                <span className="inline-block px-2 py-1 rounded-full text-xs font-medium bg-pf-info/20 text-pf-info">
                  ⏳ Queued: {model.queuedCount}
                </span>
              )}
              {model.printingCount > 0 && (
                <span className="inline-block px-2 py-1 rounded-full text-xs font-medium bg-pf-success/20 text-pf-success">
                  ▶ Printing: {model.printingCount}
                </span>
              )}
              {model.pausedCount > 0 && (
                <span className="inline-block px-2 py-1 rounded-full text-xs font-medium bg-pf-warning/20 text-pf-warning">
                  ⏸ Paused: {model.pausedCount}
                </span>
              )}
            </div>
          </div>
          <div className="text-pf-text-secondary text-2xl">
            {isExpanded ? "▼" : "▶"}
          </div>
        </div>
      </div>

      {/* Stats Row */}
      <div className="px-4 py-3 bg-pf-bg-0 border-t border-pf-border text-sm text-pf-text-secondary">
        <span className="font-medium">
          ⏱️ Avg Wait: {model.averageWaitTimeMinutes}m
        </span>
      </div>

      {/* Expandable Jobs List */}
      {isExpanded && model.jobs.length > 0 && (
        <div className="border-t border-pf-border divide-y divide-pf-border">
          {displayedJobs.map((job) => (
            <div key={job.id} className="p-3 flex items-center justify-between">
              <div className="flex-1 min-w-0">
                <div className="text-sm font-medium text-pf-text-primary truncate">
                  {job.name}
                </div>
                <div className="text-xs text-pf-text-secondary mt-1">
                  {job.estimatedTime > 0
                    ? `~${Math.round(job.estimatedTime / 60)} min`
                    : "No estimate"}
                </div>
              </div>

              {/* Job Actions */}
              <div className="flex gap-1 ml-2">
                {job.status === "queued" && (
                  <Button
                    onClick={() => handleJobAction(job.id, "pause")}
                    disabled={actingOnJobId === job.id}
                    size="sm"
                    variant="secondary"
                    title="Pause job"
                  >
                    ⏸
                  </Button>
                )}
                {job.status === "printing" && (
                  <>
                    <Button
                      onClick={() => handleJobAction(job.id, "pause")}
                      disabled={actingOnJobId === job.id}
                      size="sm"
                      variant="secondary"
                      title="Pause printing"
                    >
                      ⏸
                    </Button>
                  </>
                )}
                {job.status === "paused" && (
                  <Button
                    onClick={() => handleJobAction(job.id, "resume")}
                    disabled={actingOnJobId === job.id}
                    size="sm"
                    variant="secondary"
                    title="Resume job"
                  >
                    ▶
                  </Button>
                )}
                <Button
                  onClick={() => handleJobAction(job.id, "cancel")}
                  disabled={actingOnJobId === job.id}
                  size="sm"
                  variant="secondary"
                  title="Cancel job"
                >
                  ⊗
                </Button>
              </div>
            </div>
          ))}

          {/* Show more count if applicable */}
          {model.jobs.length > 3 && (
            <div className="p-3 text-center text-sm text-pf-text-secondary bg-pf-bg-0">
              +{model.jobs.length - 3} more job
              {model.jobs.length - 3 !== 1 ? "s" : ""}
            </div>
          )}
        </div>
      )}

      {/* Action Button */}
      <div className="p-3 border-t border-pf-border bg-pf-bg-0">
        <Button
          onClick={() => onViewAllJobs(model.name)}
          variant="primary"
          size="sm"
          className="w-full"
        >
          View All Jobs for This Model
        </Button>
      </div>
    </div>
  );
}
