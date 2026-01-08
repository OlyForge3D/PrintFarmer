import { useCallback, useEffect, useMemo, useState } from "react";
import { QueueJob } from "@/types/api";
import { printQueueService } from "@/services/printQueueService";
import { Alert } from "@/common/components/ui/Alert";
import ModelFiltersBar from "./ModelFiltersBar";
import ModelStatisticsPanel from "./ModelStatisticsPanel";
import ModelJobsCard from "./ModelJobsCard";

type JobStatus = "queued" | "printing" | "paused" | "completed" | "failed";

/**
 * Statistics calculated for a single printer model
 */
export interface ModelStats {
  name: string;
  queuedCount: number;
  printingCount: number;
  pausedCount: number;
  totalCount: number;
  averageWaitTimeMinutes: number;
  jobs: QueueJob[];
}

interface ModelFilteredJobsTabProps {
  onViewAllJobs?: (modelName: string) => void;
  onJobAction?: (jobId: string, action: JobAction) => Promise<void>;
}

export type JobAction = "pause" | "resume" | "cancel" | "priority";

/**
 * ModelFilteredJobsTab Component
 *
 * Displays all queued and printing jobs grouped by printer model.
 * Provides filtering, statistics, and job management per model.
 *
 * Features:
 * - Groups jobs by printer model
 * - Displays statistics per model (queued, printing, avg wait time)
 * - Filters by model name and status
 * - Responsive grid layout
 * - Expandable model cards with job lists
 */
export default function ModelFilteredJobsTab({
  onViewAllJobs,
  onJobAction,
}: ModelFilteredJobsTabProps) {
  // State
  const [jobs, setJobs] = useState<QueueJob[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedModel, setSelectedModel] = useState<string | null>(null);
  const [selectedStatuses, setSelectedStatuses] = useState<JobStatus[]>([
    "queued",
    "printing",
    "paused",
  ]);
  const [sortBy, setSortBy] = useState<"name" | "queue" | "waitTime" | "printing">(
    "name"
  );
  const [expandedModels, setExpandedModels] = useState<Set<string>>(
    new Set()
  );

  /**
   * Load jobs from API
   */
  const loadJobs = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const response = await printQueueService.getAllQueuedJobsAsync(
        undefined,
        undefined,
        undefined,
        100,
        0
      );
      // Convert API response to QueueJob format
      const queueJobs: QueueJob[] = response.map((job: any) => ({
        id: job.id,
        name: job.fileName,
        printerModel: job.printerModel || "Unknown",
        material: job.material || "Unknown",
        estimatedTime: job.estimatedTime || 0,
        progress: job.progress || 0,
        status: job.status?.toLowerCase() || "queued",
        createdAt: job.createdAt || new Date().toISOString(),
        startedAt: job.startedAt,
      }));
      setJobs(queueJobs);
    } catch (err) {
      setError(
        err instanceof Error
          ? err.message
          : "Failed to load jobs. Please try again."
      );
      console.error("Error loading jobs:", err);
    } finally {
      setLoading(false);
    }
  }, []);

  /**
   * Load jobs on component mount
   */
  useEffect(() => {
    loadJobs();
  }, [loadJobs]);

  /**
   * Group jobs by printer model
   */
  const groupJobsByModel = useCallback(
    (jobsList: QueueJob[]): Map<string, QueueJob[]> => {
      const grouped = new Map<string, QueueJob[]>();

      jobsList.forEach((job) => {
        const modelName = job.printerModel || "Unknown";
        if (!grouped.has(modelName)) {
          grouped.set(modelName, []);
        }
        grouped.get(modelName)!.push(job);
      });

      return grouped;
    },
    []
  );

  /**
   * Calculate statistics for each model
   */
  const calculateModelStats = useCallback(
    (grouped: Map<string, QueueJob[]>): ModelStats[] => {
      const stats: ModelStats[] = [];

      grouped.forEach((modelJobs, modelName) => {
        const queuedJobs = modelJobs.filter((j) => j.status === "queued");
        const printingJobs = modelJobs.filter((j) => j.status === "printing");
        const pausedJobs = modelJobs.filter((j) => j.status === "paused");

        // Calculate average wait time (in minutes)
        const waitTimes = queuedJobs
          .map((job) => {
            if (job.createdAt) {
              const createdTime = new Date(job.createdAt).getTime();
              return (Date.now() - createdTime) / (1000 * 60);
            }
            return 0;
          })
          .filter((time) => time > 0);

        const averageWaitTime =
          waitTimes.length > 0
            ? waitTimes.reduce((a, b) => a + b, 0) / waitTimes.length
            : 0;

        stats.push({
          name: modelName,
          queuedCount: queuedJobs.length,
          printingCount: printingJobs.length,
          pausedCount: pausedJobs.length,
          totalCount: modelJobs.length,
          averageWaitTimeMinutes: Math.round(averageWaitTime * 10) / 10,
          jobs: modelJobs,
        });
      });

      return stats;
    },
    []
  );

  /**
   * Get all unique models from jobs
   */
  const uniqueModels = useMemo(() => {
    const models = new Set(jobs.map((job) => job.printerModel || "Unknown"));
    return Array.from(models).sort();
  }, [jobs]);

  /**
   * Group, calculate stats, filter, and sort
   */
  const filteredAndSortedStats = useMemo(() => {
    const grouped = groupJobsByModel(jobs);
    let stats = calculateModelStats(grouped);

    // Apply model filter
    if (selectedModel) {
      stats = stats.filter((s) => s.name === selectedModel);
    }

    // Apply status filter to job counts
    const filteredStats = stats.map((stat) => ({
      ...stat,
      jobs: stat.jobs.filter((job) =>
        selectedStatuses.includes(job.status)
      ),
      queuedCount: selectedStatuses.includes("queued")
        ? stat.queuedCount
        : 0,
      printingCount: selectedStatuses.includes("printing")
        ? stat.printingCount
        : 0,
      pausedCount: selectedStatuses.includes("paused") ? stat.pausedCount : 0,
      totalCount: stat.jobs.filter((job) =>
        selectedStatuses.includes(job.status)
      ).length,
    }));

    // Sort
    filteredStats.sort((a, b) => {
      switch (sortBy) {
        case "queue":
          return b.queuedCount - a.queuedCount;
        case "waitTime":
          return b.averageWaitTimeMinutes - a.averageWaitTimeMinutes;
        case "printing":
          return b.printingCount - a.printingCount;
        case "name":
        default:
          return a.name.localeCompare(b.name);
      }
    });

    return filteredStats;
  }, [
    jobs,
    groupJobsByModel,
    calculateModelStats,
    selectedModel,
    selectedStatuses,
    sortBy,
  ]);

  /**
   * Toggle model expansion
   */
  const handleToggleExpand = useCallback((modelName: string) => {
    setExpandedModels((prev) => {
      const next = new Set(prev);
      if (next.has(modelName)) {
        next.delete(modelName);
      } else {
        next.add(modelName);
      }
      return next;
    });
  }, []);

  /**
   * Handle job action (pause, resume, cancel, priority)
   */
  const handleJobAction = useCallback(
    async (jobId: string, action: JobAction) => {
      if (onJobAction) {
        try {
          await onJobAction(jobId, action);
          // Reload jobs after action
          await loadJobs();
        } catch (err) {
          setError(
            err instanceof Error ? err.message : "Failed to perform action"
          );
        }
      }
    },
    [onJobAction, loadJobs]
  );

  /**
   * Handle view all jobs for model
   */
  const handleViewAllJobs = useCallback(
    (modelName: string) => {
      if (onViewAllJobs) {
        onViewAllJobs(modelName);
      }
    },
    [onViewAllJobs]
  );

  return (
    <div className="space-y-4">
      {/* Error Alert */}
      {error && (
        <Alert type="error">
          {error}
        </Alert>
      )}

      {/* Filters */}
      <ModelFiltersBar
        models={uniqueModels}
        selectedModel={selectedModel}
        onModelChange={setSelectedModel}
        selectedStatuses={selectedStatuses}
        onStatusChange={setSelectedStatuses}
        sortBy={sortBy}
        onSortChange={setSortBy}
        onRefresh={loadJobs}
        isLoading={loading}
      />

      {/* Statistics Panel */}
      <ModelStatisticsPanel
        stats={filteredAndSortedStats}
        isLoading={loading}
      />

      {/* Model Cards Grid */}
      {loading ? (
        <div className="text-center py-8">
          <div className="text-pf-text-secondary">Loading models...</div>
        </div>
      ) : filteredAndSortedStats.length === 0 ? (
        <div className="text-center py-12">
          <div className="text-pf-text-secondary text-lg">
            No models found with the selected filters
          </div>
        </div>
      ) : (
        <div className="grid grid-cols-1 lg:grid-cols-2 xl:grid-cols-3 gap-4">
          {filteredAndSortedStats.map((stat) => (
            <ModelJobsCard
              key={stat.name}
              model={stat}
              isExpanded={expandedModels.has(stat.name)}
              onToggleExpand={() => handleToggleExpand(stat.name)}
              onJobAction={handleJobAction}
              onViewAllJobs={handleViewAllJobs}
            />
          ))}
        </div>
      )}
    </div>
  );
}
