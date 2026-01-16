import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert } from "@/common/components/ui/Alert";
import { Button } from "@/common/components/ui/Button";
import HistoryFiltersBar from "./HistoryFiltersBar";
import HistoryStatisticsPanel from "./HistoryStatisticsPanel";
import HistoryJobCard from "./HistoryJobCard";
import { ConfirmationModal } from "@/common/components/modals/ConfirmationModal";
import { apiClient } from "@/services/api";
import type { HistoryJob } from "@/types/queue";
import type { QueueHistoryTabProps } from "@/types/components";

/**
 * QueueHistoryTab Component
 *
 * Displays completed, failed, and cancelled print jobs with filtering,
 * statistics, and the ability to rerun completed jobs.
 *
 * Features:
 * - Browse job history with pagination
 * - Filter by date range, status, model, material
 * - Sort by various criteria
 * - View statistics and failure reasons
 * - Rerun completed jobs
 */
export default function QueueHistoryTab({
  onRerun,
  onViewDetails,
}: QueueHistoryTabProps) {
  // State
  const [jobs, setJobs] = useState<HistoryJob[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  
  // Filter state
  const [dateStart, setDateStart] = useState<Date | null>(null);
  const [dateEnd, setDateEnd] = useState<Date | null>(null);
  const [selectedStatuses, setSelectedStatuses] = useState<string[]>(["completed", "failed", "cancelled"]);
  const [sortBy, setSortBy] = useState<"newest" | "oldest" | "duration" | "model">("newest");
  
  // Pagination
  const [currentPage, setCurrentPage] = useState(0);
  const pageSize = 50;
  
  // Modal
  const [rerunJobId, setRerunJobId] = useState<string | null>(null);

  /**
   * Load history from API
   */
  const loadHistory = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      
      const response = await apiClient.getAnalyticsQueueHistory(pageSize, currentPage * pageSize, sortBy);
      
      // Convert API response to HistoryJob format
      const historyJobs: HistoryJob[] = (response?.entries || []).map((job) => ({
        id: job.id,
        name: job.jobName,
        printerName: job.printerName || "Unknown",
        status: (job.status?.toLowerCase() || "completed") as "completed" | "failed" | "cancelled",
        completionPercentage: job.completionPercentage || 0,
        startedAt: job.startedAtUtc || new Date().toISOString(),
        completedAt: job.completedAtUtc || null,
        durationSeconds: job.actualPrintTimeSeconds || 0,
        failureReason: job.failureReason,
      }));
      
      setJobs(historyJobs);
      setTotalCount(response?.totalCount || 0);
    } catch (err) {
      setError(
        err instanceof Error
          ? err.message
          : "Failed to load history. Please try again."
      );
      console.error("Error loading history:", err);
    } finally {
      setLoading(false);
    }
  }, [currentPage, sortBy, pageSize]);

  /**
   * Load history on mount and when pagination/sort changes
   */
  useEffect(() => {
    loadHistory();
  }, [loadHistory]);

  /**
   * Calculate statistics
   */
  const stats = useMemo(() => {
    const completed = jobs.filter(j => j.status === "completed").length;
    const failed = jobs.filter(j => j.status === "failed").length;
    const cancelled = jobs.filter(j => j.status === "cancelled").length;
    const total = completed + failed + cancelled;
    
    const successRate = total > 0 ? Math.round((completed / total) * 100) : 0;
    
    const avgDuration = jobs.length > 0
      ? Math.round(jobs.reduce((sum, j) => sum + j.durationSeconds, 0) / jobs.length / 60)
      : 0;
    
    const failureReasons: { [key: string]: number } = {};
    jobs.forEach(job => {
      if (job.failureReason && job.status === "failed") {
        failureReasons[job.failureReason] = (failureReasons[job.failureReason] || 0) + 1;
      }
    });
    
    return {
      totalCompleted: completed,
      totalFailed: failed,
      totalCancelled: cancelled,
      successRate,
      averageDurationMinutes: avgDuration,
      failureReasons,
    };
  }, [jobs]);

  /**
   * Filter jobs by status
   */
  const filteredJobs = useMemo(() => {
    let filtered = jobs;
    
    // Filter by status
    if (selectedStatuses.length > 0) {
      filtered = filtered.filter(j => selectedStatuses.includes(j.status));
    }
    
    // Filter by date range
    if (dateStart || dateEnd) {
      filtered = filtered.filter(j => {
        const jobDate = new Date(j.completedAt || j.startedAt);
        if (dateStart && jobDate < dateStart) return false;
        if (dateEnd && jobDate > dateEnd) return false;
        return true;
      });
    }
    
    return filtered;
  }, [jobs, selectedStatuses, dateStart, dateEnd]);

  /**
   * Handle rerun confirmation
   */
  const handleRerunConfirm = async () => {
    if (!rerunJobId) return;
    
    try {
      if (onRerun) {
        await onRerun(rerunJobId);
      }
      setRerunJobId(null);
      // Reload history after rerun
      await loadHistory();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to rerun job");
    }
  };

  const totalPages = Math.ceil(totalCount / pageSize);
  const hasNextPage = currentPage < totalPages - 1;
  const hasPrevPage = currentPage > 0;

  return (
    <div className="space-y-4">
      {/* Error Alert */}
      {error && (
        <Alert type="error">
          {error}
        </Alert>
      )}

      {/* Filters */}
      <HistoryFiltersBar
        selectedStatuses={selectedStatuses}
        onStatusChange={setSelectedStatuses}
        dateStart={dateStart}
        onDateStartChange={setDateStart}
        dateEnd={dateEnd}
        onDateEndChange={setDateEnd}
        sortBy={sortBy}
        onSortChange={setSortBy}
        onRefresh={loadHistory}
        isLoading={loading}
      />

      {/* Statistics Panel */}
      <HistoryStatisticsPanel stats={stats} isLoading={loading} />

      {/* History Jobs Grid */}
      {loading ? (
        <div className="text-center py-8">
          <div className="text-pf-text-secondary">Loading history...</div>
        </div>
      ) : filteredJobs.length === 0 ? (
        <div className="flex flex-col justify-center items-center py-16 bg-pf-bg-1 border border-pf-border rounded-lg">
          <div className="flex flex-col items-center gap-4 text-center">
            <div className="w-16 h-16 rounded-full bg-pf-bg-2 flex items-center justify-center">
              <span className="text-3xl">📜</span>
            </div>
            <div>
              <h3 className="text-lg font-semibold text-pf-text-primary mb-2">No Job History</h3>
              <p className="text-pf-text-secondary max-w-md">
                Completed and failed jobs will appear here. Start by queueing and printing a job.
              </p>
            </div>
          </div>
        </div>
      ) : (
        <>
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            {filteredJobs.map((job) => (
              <HistoryJobCard
                key={job.id}
                job={job}
                onRerun={() => setRerunJobId(job.id)}
                onViewDetails={onViewDetails}
              />
            ))}
          </div>

          {/* Pagination */}
          <div className="flex items-center justify-between py-4">
            <div className="text-sm text-pf-text-secondary">
              Page {currentPage + 1} of {Math.max(1, totalPages)} | {totalCount} total jobs
            </div>
            <div className="flex gap-2">
              <Button
                onClick={() => setCurrentPage(p => Math.max(0, p - 1))}
                disabled={!hasPrevPage || loading}
                variant="secondary"
              >
                ← Previous
              </Button>
              <Button
                onClick={() => setCurrentPage(p => p + 1)}
                disabled={!hasNextPage || loading}
                variant="secondary"
              >
                Next →
              </Button>
            </div>
          </div>
        </>
      )}

      {/* Rerun Confirmation Modal */}
      <ConfirmationModal
        isOpen={rerunJobId !== null}
        title="Rerun Print Job"
        message="Are you sure you want to rerun this completed job? It will be added to the print queue."
        confirmButtonText="Rerun Job"
        cancelButtonText="Cancel"
        isDangerous={false}
        onConfirm={handleRerunConfirm}
        onCancel={() => setRerunJobId(null)}
      />
    </div>
  );
}
