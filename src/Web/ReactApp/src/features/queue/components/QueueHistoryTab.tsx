import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert } from "@/common/components/ui/Alert";
import { Button } from "@/common/components/ui/Button";
import HistoryFiltersBar from "./HistoryFiltersBar";
import HistoryStatisticsPanel from "./HistoryStatisticsPanel";
import HistoryJobCard from "./HistoryJobCard";
import HistoryJobTable from "./HistoryJobTable";
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
  const [serverStats, setServerStats] = useState<{
    totalCompleted: number;
    totalFailed: number;
    totalCancelled: number;
    successRate: number;
    averageDurationMinutes: number;
    totalPrintTimeMinutes: number;
  } | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  
  // Filter state - default to last 7 days
  const [dateStart, setDateStart] = useState<Date | null>(() => {
    const date = new Date();
    date.setDate(date.getDate() - 7);
    date.setHours(0, 0, 0, 0);
    return date;
  });
  const [dateEnd, setDateEnd] = useState<Date | null>(() => {
    const date = new Date();
    date.setHours(23, 59, 59, 999);
    return date;
  });
  const [selectedStatuses, setSelectedStatuses] = useState<string[]>(["completed", "failed", "cancelled"]);
  const [sortBy, setSortBy] = useState<"newest" | "oldest" | "duration" | "model">("newest");
  
  // Pagination
  const [currentPage, setCurrentPage] = useState(0);
  const pageSize = 50;
  
  // Modal
  const [rerunJobId, setRerunJobId] = useState<string | null>(null);
  
  // View mode (cards or table) - persisted to localStorage
  const STORAGE_KEY_VIEW_MODE = 'printfarmer-queue-history-viewmode';
  const [viewMode, setViewModeState] = useState<"cards" | "table">(() => {
    const saved = localStorage.getItem(STORAGE_KEY_VIEW_MODE);
    return saved === 'cards' || saved === 'table' ? saved : 'table';
  });
  
  const setViewMode = useCallback((mode: "cards" | "table") => {
    setViewModeState(mode);
    localStorage.setItem(STORAGE_KEY_VIEW_MODE, mode);
  }, []);

  /**
   * Load history from API with filters
   */
  const loadHistory = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      
      // Pass all filters to API
      const response = await apiClient.getAnalyticsQueueHistory(
        pageSize,
        currentPage * pageSize,
        sortBy,
        selectedStatuses.length > 0 ? selectedStatuses : undefined,
        dateStart?.toISOString() ?? null,
        dateEnd?.toISOString() ?? null
      );
      
      // Convert API response to HistoryJob format
      interface HistoryEntryResponse {
        id: string;
        jobName: string;
        printerName?: string;
        status?: string;
        completionPercentage?: number;
        startedAtUtc?: string;
        completedAtUtc?: string;
        actualPrintTimeSeconds?: number;
        failureReason?: string;
      }
      
      const entries = (response?.entries || []) as HistoryEntryResponse[];
      const historyJobs: HistoryJob[] = entries.map((job) => ({
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
      setServerStats(response?.stats || null);
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
  }, [currentPage, sortBy, pageSize, selectedStatuses, dateStart, dateEnd]);

  /**
   * Load history on mount and when filters change
   * Reset to first page when filters change (except pagination)
   */
  useEffect(() => {
    loadHistory();
  }, [loadHistory]);

  /**
   * Reset to first page when filters change
   */
  useEffect(() => {
    setCurrentPage(0);
  }, [selectedStatuses, dateStart, dateEnd, sortBy]);

  /**
   * Use server-side statistics for the full filtered result set
   * Falls back to client-side calculation if server stats unavailable
   */
  const stats = useMemo(() => {
    // Use server-side stats if available (covers entire filtered result set)
    if (serverStats) {
      return {
        totalCompleted: serverStats.totalCompleted,
        totalFailed: serverStats.totalFailed,
        totalCancelled: serverStats.totalCancelled,
        successRate: serverStats.successRate,
        averageDurationMinutes: serverStats.averageDurationMinutes,
        failureReasons: {} as { [key: string]: number }, // Not available from server yet
      };
    }
    
    // Fallback to client-side calculation (only for current page)
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
  }, [serverStats, jobs]);

  /**
   * Jobs are now filtered server-side, so we use them directly
   * (Client-side filtering removed - all filtering done in API)
   */
  const filteredJobs = jobs;

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
        viewMode={viewMode}
        onViewModeChange={setViewMode}
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
          {viewMode === "table" ? (
            <HistoryJobTable
              jobs={filteredJobs}
              onRerun={(jobId) => setRerunJobId(jobId)}
              onViewDetails={onViewDetails}
            />
          ) : (
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
          )}

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
