import { useEffect, useState, useCallback, useRef } from "react";
import { useSearchParams, useParams, useNavigate } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { PageTemplate } from "@/common/components/PageTemplate";
import { Alert } from "@/common/components/ui/Alert";
import { ConfirmationModal } from "@/common/components/modals/ConfirmationModal";
import { Tabs } from "@/common/components/ui/Tabs";
import { useKeyboardNavigation } from "@/common/hooks/useKeyboardNavigation";
import { useKeyboardShortcuts } from "@/common/hooks/useKeyboardShortcuts";
import { TableFiltersBar } from "../components/QueueFiltersBar";
import { QueueJobsTable } from "../components/QueueJobsTable";
import { QueueJobsCardView, QueueJobsListView } from "../components/QueueJobsCollectionViews";
import { QueueViewModeSelector, type QueueViewMode } from "../components/QueueViewModeSelector";
import JobDetailsModal from "../components/JobDetailsModal";
import QueueHistoryTab from "../components/QueueHistoryTab";
import DispatchLogTab from "../components/DispatchLogTab";
import QueueTimelineTab from "../components/QueueTimelineTab";
import QueueDateRangeBar, { defaultDateRange } from "../components/QueueDateRangeBar";
import type { DateRange } from "../components/QueueDateRangeBar";
import { SpoolValidationModal } from "../components/SpoolValidationModal";
import { AutoDispatchGlobalToggle } from "../components/AutoDispatchGlobalToggle";
import { validateSpoolForDispatch } from "../utils/spoolValidation";
import type { SpoolValidationContext } from "../utils/spoolValidation";
import {
  advanceDispatchUploadFence,
  fenceDispatchAttempt,
} from "../utils/dispatchUploadFence";
import type { DispatchUploadFence } from "../utils/dispatchUploadFence";
import { ScheduleModal } from "@/features/scheduling/components/ScheduleModal";
import { apiClient } from "@/services/api";
import { printerSignalRService } from "@/services/printer-signalr";
import { usePageTour } from "@/common/hooks/usePageTour";
import { printQueueTour } from "@/features/queue/tours/print-queue.tour";
import { HelpButton } from "@/common/components/HelpButton";
import { mergePrinterProgress, mergePrinterThumbnail } from "@/features/queue/utils/printerProgress";
import {
  mutationErrorMessage,
  mutationErrorStatus,
} from "@/common/utils/mutationError";
import { queueSummariesFleetQueryKey } from "@/features/printers/hooks/useQueueSummariesFleet";
import { PrintJobPriority, type DispatchUploadProgressDto } from "@/types/api";
import type {
  QueuedPrintJobWithFileMetaDto,
  QueueStatsDto,
} from "@/types/api";

// localStorage keys for persisting user preferences
const STORAGE_KEY_ACTIVE_TAB = 'printfarmer-queue-active-tab';
const STORAGE_KEY_QUEUE_VIEW_MODE = 'printfarmer-queue-view-mode';
const VALID_TABS = ['print-queue', 'timeline', 'history', 'dispatch-log'] as const;
const VALID_QUEUE_VIEW_MODES: QueueViewMode[] = ["table", "list", "cards"];

export function PrintQueueDashboardPage() {
  const queryClient = useQueryClient();
  const [searchParams] = useSearchParams();
  const { tabId } = useParams<{ tabId?: string }>();
  const navigate = useNavigate();
  const { startTour } = usePageTour({ tourId: 'print-queue', steps: printQueueTour });
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [modelFilter, setModelFilter] = useState<string | null>(null);
  const [materialFilter, setMaterialFilter] = useState<string | null>(null);
  const [sortBy, setSortBy] = useState<"priority" | "deadline" | "deadline_desc">("priority");
  const [dateRange, setDateRange] = useState<DateRange>(defaultDateRange);
  const [showCancelConfirmation, setShowCancelConfirmation] = useState(false);
  const [jobToCancel, setJobToCancel] = useState<{
    jobId: string;
    rowVersion: string;
  } | null>(null);
  const [cancelingJobId, setCancelingJobId] = useState<string | null>(null);
  const [queueViewMode, setQueueViewModeState] = useState<QueueViewMode>(() => {
    const saved = localStorage.getItem(STORAGE_KEY_QUEUE_VIEW_MODE);
    return saved && VALID_QUEUE_VIEW_MODES.includes(saved as QueueViewMode) ? (saved as QueueViewMode) : "table";
  });
  
  // Persist active tab — URL path takes priority, then search param, then localStorage
  const [activeTab, setActiveTabState] = useState(() => {
    if (tabId && VALID_TABS.includes(tabId as typeof VALID_TABS[number])) return tabId;
    const fromUrl = searchParams.get('tab');
    if (fromUrl && VALID_TABS.includes(fromUrl as typeof VALID_TABS[number])) return fromUrl;
    const saved = localStorage.getItem(STORAGE_KEY_ACTIVE_TAB);
    return saved && VALID_TABS.includes(saved as typeof VALID_TABS[number]) ? saved : 'print-queue';
  });
  
  const setActiveTab = useCallback((tab: string) => {
    setActiveTabState(tab);
    localStorage.setItem(STORAGE_KEY_ACTIVE_TAB, tab);
    navigate(`/printQueue/${tab}`, { replace: true });
  }, [navigate]);

  const setQueueViewMode = useCallback((mode: QueueViewMode) => {
    setQueueViewModeState(mode);
    localStorage.setItem(STORAGE_KEY_QUEUE_VIEW_MODE, mode);
  }, []);
  
  const [isJobDetailsModalOpen, setIsJobDetailsModalOpen] = useState(false);
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null);
  const [showDetailPanel, setShowDetailPanel] = useState(false);
  const [dispatchingJobId, setDispatchingJobId] = useState<string | null>(null);
  const [dispatchUploadProgressByJobId, setDispatchUploadProgressByJobId] = useState<
    Record<string, DispatchUploadProgressDto>
  >({});
  const uploadAttemptFenceByJobId = useRef<
    Record<string, DispatchUploadFence>
  >({});
  const reviewRequiredJobIds = useRef(new Set<string>());
  const [printProgressByPrinterId, setPrintProgressByPrinterId] = useState<Record<string, number>>({});
  const [printThumbnailByPrinterId, setPrintThumbnailByPrinterId] = useState<Record<string, string>>({});
  const [scheduleModalJobId, setScheduleModalJobId] = useState<string | null>(null);
  const [mismatchReviewedRowVersion, setMismatchReviewedRowVersion] =
    useState<string | null>(null);
  const [spoolValidationCtx, setSpoolValidationCtx] = useState<SpoolValidationContext | null>(null);

  const { data: jobs = [], isLoading: loading, isFetching: isRefreshing, error: jobsError } = useQuery({
    // The active Print Queue reflects current state, so it is intentionally NOT
    // constrained by the page date range (which applies to Timeline/History/Dispatch Log).
    // Date-filtering active jobs previously hid still-queued/assigned jobs older than the
    // window while the stats chiclet still counted them, causing a count/list mismatch.
    queryKey: ['queue-jobs', statusFilter, modelFilter, materialFilter, sortBy],
    queryFn: () => apiClient.getAnalyticsQueueJobs(
      statusFilter || undefined,
      modelFilter || undefined,
      materialFilter || undefined,
      sortBy,
      100,
      0,
      undefined,
      undefined,
    ) as Promise<QueuedPrintJobWithFileMetaDto[]>,
    staleTime: 10_000,
    refetchInterval: 10_000,
  });

  const { data: stats = null } = useQuery({
    queryKey: ['queue-stats'],
    queryFn: () => apiClient.getAnalyticsQueueStats() as Promise<QueueStatsDto>,
    staleTime: 10_000,
    refetchInterval: 10_000,
  });

  const invalidateQueue = useCallback(async () => {
    setError(null);
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['queue-jobs'] }),
      queryClient.invalidateQueries({ queryKey: ['queue-stats'] }),
      queryClient.invalidateQueries({ queryKey: ['job-queue'] }),
      // Canonical fleet queue-summary key (#1146 item 9): every queue
      // mutation on this page (dispatch, cancel, pause/resume, priority,
      // bulk cancel, etc.) funnels through this one invalidator, so the
      // compact printer cards' "X of Y" labels stay in step with it too.
      queryClient.invalidateQueries({ queryKey: queueSummariesFleetQueryKey }),
    ]);
  }, [queryClient]);

  const requireFreshReview = useCallback(async (
    jobIds: string[],
    message?: string
  ) => {
    jobIds.forEach((jobId) => {
      reviewRequiredJobIds.current.add(jobId);
      delete uploadAttemptFenceByJobId.current[jobId];
    });
    setJobToCancel(null);
    setShowCancelConfirmation(false);
    setMismatchReviewedRowVersion(null);
    setSpoolValidationCtx(null);
    setDispatchUploadProgressByJobId((previous) => {
      const next = { ...previous };
      jobIds.forEach((jobId) => delete next[jobId]);
      return next;
    });
    await invalidateQueue();
    setError(
      message ??
        "This job changed after you reviewed it. Review refreshed state and confirm again."
    );
  }, [invalidateQueue]);

  const handleReviewedMutationFailure = useCallback(async (
    error: unknown,
    jobIds: string[],
    fallback: string
  ) => {
    const status = mutationErrorStatus(error);
    if (status === 412 || status === 428) {
      await requireFreshReview(
        jobIds,
        mutationErrorMessage(error, fallback)
      );
      return;
    }
    setError(mutationErrorMessage(error, fallback));
  }, [requireFreshReview]);

  const confirmRefreshedIntent = useCallback((
    jobIds: string[],
    action: string
  ) => {
    const requiresReview = jobIds.some((jobId) =>
      reviewRequiredJobIds.current.has(jobId)
    );
    if (!requiresReview) {
      return true;
    }
    const confirmed = window.confirm(
      `This job changed after your previous ${action} attempt. Confirm ${action} using the refreshed row?`
    );
    if (confirmed) {
      jobIds.forEach((jobId) => reviewRequiredJobIds.current.delete(jobId));
    }
    return confirmed;
  }, []);

  useEffect(() => {
    const visibleJobIds = new Set(jobs.map((entry) => entry.job.id));
    for (const [jobId] of Object.entries(uploadAttemptFenceByJobId.current)) {
      if (!visibleJobIds.has(jobId)) {
        delete uploadAttemptFenceByJobId.current[jobId];
      }
    }
    for (const entry of jobs) {
      const dispatch = entry.job.dispatchResult;
      if (dispatch?.attemptId && dispatch.attemptNumber != null) {
        uploadAttemptFenceByJobId.current[entry.job.id] =
          fenceDispatchAttempt(
            uploadAttemptFenceByJobId.current[entry.job.id],
            dispatch.attemptId,
            dispatch.attemptNumber
          );
      }
    }
    setDispatchUploadProgressByJobId((previous) => {
      let changed = false;
      const next = { ...previous };
      for (const [jobId, progress] of Object.entries(previous)) {
          const fence = uploadAttemptFenceByJobId.current[jobId];
          if (
            !visibleJobIds.has(jobId) ||
            !fence ||
            fence.attemptId !== progress.attemptId ||
            fence.attemptNumber !== progress.attemptNumber
          ) {
            delete next[jobId];
            changed = true;
          }
      }
      return changed ? next : previous;
    });
  }, [jobs]);

  const displayError = error || (jobsError ? (jobsError instanceof Error ? jobsError.message : "Failed to load jobs") : null);

  // Keyboard navigation for job list (only for active tab)
  const { selectedIndex } = useKeyboardNavigation(
    jobs,
    (job: QueuedPrintJobWithFileMetaDto) => {
      setSelectedJobId(job.job.id);
      setShowDetailPanel(true);
      setIsJobDetailsModalOpen(true);
    },
    {
      columns: 1,  // Single column table-like layout
      onEscapeKey: () => {
        setShowDetailPanel(false);
        setIsJobDetailsModalOpen(false);
      }
    }
  );

  // Keyboard shortcuts for queue actions
  useKeyboardShortcuts([
    {
      key: 'd',
      handler: () => {
        if (selectedIndex >= 0 && jobs[selectedIndex]) {
          handleCancelJob(jobs[selectedIndex].job.id);
        }
      },
      description: 'Cancel selected job'
    },
    {
      key: 'p',
      handler: async () => {
        if (selectedIndex >= 0 && jobs[selectedIndex]) {
          const job = jobs[selectedIndex];
          if (job.job.status === 'Printing') {
            await handlePauseJob(job.job.id);
          } else if (job.job.status === 'Paused') {
            await handleResumeJob(job.job.id);
          }
        }
      },
      description: 'Pause/resume selected job'
    },
    {
      key: 's',
      handler: async () => {
        if (selectedIndex >= 0 && jobs[selectedIndex]) {
          const job = jobs[selectedIndex];
          // Only dispatch if job is in Queued or Assigned state and has an assigned printer
          if ((job.job.status === 'Queued' || job.job.status === 'Assigned') && job.assignedPrinter) {
            await handleDispatchJob(job.job.id);
          }
        }
      },
      description: 'Start print (dispatch selected job)'
    },
    {
      key: 'v',
      handler: () => {
        setShowDetailPanel(!showDetailPanel);
      },
      description: 'Toggle detail panel visibility'
    }
  ]);

  const handleCancelJob = useCallback((jobId: string) => {
    if (!confirmRefreshedIntent([jobId], "cancellation")) {
      return;
    }
    const reviewed = jobs.find((entry) => entry.job.id === jobId)?.job.rowVersion;
    if (!reviewed) {
      setError("This job has no reviewed revision. Refresh and review it again.");
      return;
    }
    setJobToCancel({ jobId, rowVersion: reviewed });
    setShowCancelConfirmation(true);
  }, [confirmRefreshedIntent, jobs]);

  const handleConfirmCancel = useCallback(async () => {
    if (!jobToCancel) return;

    try {
      setCancelingJobId(jobToCancel.jobId);
      await apiClient.cancelPrintQueueJob(
        jobToCancel.jobId,
        jobToCancel.rowVersion
      );
      setShowCancelConfirmation(false);
      setJobToCancel(null);
      invalidateQueue();
    } catch (err) {
      setShowCancelConfirmation(false);
      setJobToCancel(null);
      await handleReviewedMutationFailure(
        err,
        [jobToCancel.jobId],
        "Failed to cancel job"
      );
    } finally {
      setCancelingJobId(null);
    }
  }, [jobToCancel, handleReviewedMutationFailure, invalidateQueue]);

  const handlePauseJob = async (jobId: string) => {
    if (!confirmRefreshedIntent([jobId], "pause")) return;
    try {
      const rowVersion = jobs.find((entry) => entry.job.id === jobId)?.job.rowVersion;
      if (!rowVersion) throw new Error("Refresh and review this job before pausing it.");
      await apiClient.pauseJob(jobId, rowVersion);
      invalidateQueue();
    } catch (err) {
      await handleReviewedMutationFailure(
        err,
        [jobId],
        "Failed to pause job"
      );
    }
  };

  const handleResumeJob = useCallback(async (jobId: string) => {
    if (!confirmRefreshedIntent([jobId], "resume")) return;
    try {
      const rowVersion = jobs.find((entry) => entry.job.id === jobId)?.job.rowVersion;
      if (!rowVersion) throw new Error("Refresh and review this job before resuming it.");
      await apiClient.resumeJob(jobId, rowVersion);
      invalidateQueue();
    } catch (err) {
      await handleReviewedMutationFailure(
        err,
        [jobId],
        "Failed to resume job"
      );
    }
  }, [
    confirmRefreshedIntent,
    handleReviewedMutationFailure,
    invalidateQueue,
    jobs,
  ]);

  const handleAbortPrint = useCallback(async (jobId: string) => {
    if (!confirmRefreshedIntent([jobId], "abort")) return;
    try {
      const rowVersion = jobs.find((entry) => entry.job.id === jobId)?.job.rowVersion;
      if (!rowVersion) throw new Error("Refresh and review this job before aborting it.");
      await apiClient.abortPrint(jobId, rowVersion);
      invalidateQueue();
    } catch (err) {
      await handleReviewedMutationFailure(
        err,
        [jobId],
        "Failed to abort print"
      );
    }
  }, [
    confirmRefreshedIntent,
    handleReviewedMutationFailure,
    invalidateQueue,
    jobs,
  ]);

  /** Actually send the dispatch request for a job (no spool check). */
  const executeDispatch = useCallback(async (
    jobId: string,
    reviewedRowVersion: string
  ) => {
    setDispatchingJobId(jobId);
    setDispatchUploadProgressByJobId((prev) => {
      const copy = { ...prev };
      delete copy[jobId];
      return copy;
    });
    try {
      const result = await apiClient.dispatchPrintQueueJob(
        jobId,
        reviewedRowVersion
      );
      if (result.kind === "stale") {
        await requireFreshReview(
          [jobId],
          "This job changed after you reviewed it. Review the refreshed row before confirming again."
        );
        return;
      }
      if (result.kind === "reconciliation") {
        setError(
          result.dispatch.errorDetail ??
            "The backend outcome is unknown. The attempt remains fenced while reconciliation runs."
        );
      } else if (result.kind === "conflict" || result.kind === "unavailable") {
        setError(
          `${result.errorCode}: ${result.detail ?? "Dispatch was not accepted."}${
            result.job?.dispatchResult?.isRetryable ? " Retry after reviewing refreshed state." : ""
          }`
        );
      }
      if (
        (result.kind === "accepted" || result.kind === "reconciliation") &&
        result.dispatch.attemptId &&
        result.dispatch.attemptNumber != null
      ) {
        uploadAttemptFenceByJobId.current[jobId] = fenceDispatchAttempt(
          uploadAttemptFenceByJobId.current[jobId],
          result.dispatch.attemptId,
          result.dispatch.attemptNumber
        );
      }
      invalidateQueue();
    } catch (err) {
      await handleReviewedMutationFailure(
        err,
        [jobId],
        "Failed to start print job"
      );
    } finally {
      setDispatchingJobId(null);
      setDispatchUploadProgressByJobId((prev) => {
        const copy = { ...prev };
        delete copy[jobId];
        return copy;
      });
    }
  }, [handleReviewedMutationFailure, invalidateQueue, requireFreshReview]);

  /** Validate spool state before dispatching; shows modal if issues found. */
  const handleDispatchJob = useCallback(async (jobId: string) => {
    if (!confirmRefreshedIntent([jobId], "dispatch")) return;
    const jobWrapper = jobs.find(j => j.job.id === jobId);
    const reviewedRowVersion = jobWrapper?.job.rowVersion;
    if (!reviewedRowVersion) {
      setError("This job has no reviewed revision. Refresh and review it again.");
      return;
    }
    if (!jobWrapper?.assignedPrinter) {
      await executeDispatch(jobId, reviewedRowVersion);
      return;
    }

    setDispatchingJobId(jobId);
    try {
      const ctx = await validateSpoolForDispatch(
        {
          id: jobWrapper.job.id,
          name: jobWrapper.job.name || jobWrapper.gcodeFile?.name,
          requiredMaterialType: jobWrapper.job.requiredMaterialType || jobWrapper.gcodeFile?.materialType,
        },
        { id: jobWrapper.assignedPrinter.id, name: jobWrapper.assignedPrinter.name },
      );

      if (ctx) {
        // Spool issue found — show validation modal
        setSpoolValidationCtx(ctx);
        setMismatchReviewedRowVersion(reviewedRowVersion);
        setDispatchingJobId(null);
      } else {
        // No issues — dispatch directly
        setDispatchingJobId(null);
        await executeDispatch(jobId, reviewedRowVersion);
      }
    } catch {
      // Validation fetch failed — don't block dispatch
      setDispatchingJobId(null);
      await executeDispatch(jobId, reviewedRowVersion);
    }
  }, [confirmRefreshedIntent, jobs, executeDispatch]);

  // Dispatch upload progress subscription (SignalR)
  useEffect(() => {
    printerSignalRService.connect();
    const unsub = printerSignalRService.onDispatchUploadProgress((progress) => {
      const current = uploadAttemptFenceByJobId.current[progress.jobId];
      const nextFence = advanceDispatchUploadFence(current, progress);
      if (!nextFence) {
        return;
      }

      uploadAttemptFenceByJobId.current[progress.jobId] = nextFence;
      setDispatchUploadProgressByJobId((previous) => {
        if (progress.isCompleted) {
          const next = { ...previous };
          delete next[progress.jobId];
          return next;
        }
        return {
          ...previous,
          [progress.jobId]: progress,
        };
      });
    });
    return () => {
      unsub();
    };
  }, []);

  // Live print progress subscription (SignalR printer status) — keyed by printer id.
  useEffect(() => {
    printerSignalRService.connect();

    const applyStatus = (
      printerId: string,
      progress?: number,
      thumbnailUrl?: string,
      state?: string,
    ) => {
      // Backend progress is "sticky" (a finished printer keeps reporting its last
      // numeric progress), so activeness must be derived from the printer's
      // normalized state, not from progress presence. Only a printer that is
      // actively Printing/Paused should retain a cached live thumbnail; anything
      // else clears it so a finished job's artwork can't bleed into the next job.
      const isActive = state === "Printing" || state === "Paused";
      setPrintProgressByPrinterId((prev) => mergePrinterProgress(prev, printerId, progress));
      setPrintThumbnailByPrinterId((prev) => mergePrinterThumbnail(prev, printerId, thumbnailUrl, isActive));
    };

    // Seed from any statuses already received before this effect mounted.
    printerSignalRService.getLastStatuses().forEach((status, printerId) => {
      applyStatus(printerId, status.progress, status.thumbnailUrl, status.state);
    });

    const unsub = printerSignalRService.onPrinterStatusUpdate((status) => {
      applyStatus(status.id, status.progress, status.thumbnailUrl, status.state);
    });
    return () => {
      unsub();
    };
  }, []);

  const handleRerunJob = async (jobId: string) => {
    if (!confirmRefreshedIntent([jobId], "rerun")) return;
    try {
      const rowVersion = jobs.find((entry) => entry.job.id === jobId)?.job.rowVersion;
      if (!rowVersion) {
        throw new Error("This job has no reviewed revision. Refresh and review again.");
      }
      await apiClient.rerunPrintQueueJob(jobId, rowVersion);
      setError(null);
      invalidateQueue();
    } catch (err) {
      await handleReviewedMutationFailure(
        err,
        [jobId],
        "Failed to rerun job"
      );
    }
  };

  const handlePriorityChange = async (jobId: string, newPriority: PrintJobPriority) => {
    if (!confirmRefreshedIntent([jobId], "priority update")) return;
    try {
      const rowVersion = jobs.find((entry) => entry.job.id === jobId)?.job.rowVersion;
      if (!rowVersion) {
        throw new Error("This job has no reviewed revision. Refresh and review again.");
      }
      await apiClient.updateJobPriority(jobId, newPriority, rowVersion);
      invalidateQueue();
    } catch (err) {
      await handleReviewedMutationFailure(
        err,
        [jobId],
        "Failed to update priority"
      );
    }
  };

  const handleCloseJobDetailsModal = () => {
    setIsJobDetailsModalOpen(false);
    setSelectedJobId(null);
  };

  const handleJobDetailsSaved = () => {
    handleCloseJobDetailsModal();
    invalidateQueue();
  };

  return (
    <PageTemplate
      title="Print Queue Dashboard"
      subtitle="View and manage all queued and printing jobs"
      titleActions={<HelpButton onClick={startTour} />}
    >
      {displayError && (
        <Alert type="error" className="mb-4">
          {displayError}
        </Alert>
      )}

      {/* Stats Summary */}
      {stats && (
        <div data-tour="queue-stats" className="grid grid-cols-1 md:grid-cols-4 gap-4 mb-6">
          <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
            <div className="text-pf-text-secondary text-sm font-medium">Queued Jobs</div>
            <div className="text-3xl font-bold text-pf-info">
              {stats.totalQueued}
            </div>
          </div>
          <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
            <div className="text-pf-text-secondary text-sm font-medium">
              Currently Printing
            </div>
            <div className="text-3xl font-bold text-pf-success">
              {stats.totalPrinting}
            </div>
          </div>
          <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
            <div className="text-pf-text-secondary text-sm font-medium">Paused Jobs</div>
            <div className="text-3xl font-bold text-pf-warning">
              {stats.totalPaused}
            </div>
          </div>
          <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
            <div className="text-pf-text-secondary text-sm font-medium">
              Average Wait Time
            </div>
            <div className="text-3xl font-bold text-pf-text-secondary">
              {Math.round(stats.averageWaitTimeMinutes)}m
            </div>
          </div>
        </div>
      )}

      {/* Date range applies to Timeline/History/Dispatch Log only. The Print Queue tab
          shows current active state and is not date-filtered, so hide the bar there. */}
      {activeTab !== "print-queue" && (
        <QueueDateRangeBar
          dateFrom={dateRange.from}
          dateTo={dateRange.to}
          onChange={setDateRange}
        />
      )}

      {/* Tabbed Interface */}
      <Tabs activeTab={activeTab} onTabChange={setActiveTab}>
        <div data-tour="queue-tabs">
          <Tabs.List className="overflow-x-auto">
            <Tabs.Tab id="print-queue">Print Queue</Tabs.Tab>
            <Tabs.Tab id="timeline">Timeline</Tabs.Tab>
            <Tabs.Tab id="history">History</Tabs.Tab>
            <Tabs.Tab id="dispatch-log">Dispatch Log</Tabs.Tab>
          </Tabs.List>
        </div>

        <Tabs.Panels>
          {/* Tab 1: Queue */}
          <Tabs.Panel id="print-queue">
            <div className="flex flex-col h-full w-full min-h-0">
              {/* Filters + Auto-dispatch global toggle */}
              <div data-tour="queue-filters" className="shrink-0 p-4 border-b border-pf-border bg-pf-bg-1">
                <div className="flex items-center justify-between gap-4 flex-wrap">
                  <div className="flex-1 min-w-0">
                    <TableFiltersBar
                      onStatusChange={setStatusFilter}
                      onModelChange={setModelFilter}
                      onMaterialChange={setMaterialFilter}
                      onSortChange={setSortBy}
                      onRefresh={invalidateQueue}
                      isLoading={loading || isRefreshing}
                    />
                  </div>
                  <div className="flex items-center gap-2 shrink-0">
                    <QueueViewModeSelector value={queueViewMode} onChange={setQueueViewMode} />
                    <AutoDispatchGlobalToggle />
                  </div>
                </div>
              </div>

              {/* Queue Jobs */}
              <div data-tour="queue-jobs-table" className="flex-1 overflow-auto bg-pf-bg-1 p-4 min-h-0">
                {queueViewMode === "table" ? (
                  <QueueJobsTable
                    jobs={jobs}
                    isLoading={loading}
                    dispatchingJobId={dispatchingJobId}
                    cancelingJobId={cancelingJobId}
                    dispatchUploadProgressByJobId={dispatchUploadProgressByJobId}
                    printProgressByPrinterId={printProgressByPrinterId}
                    printThumbnailByPrinterId={printThumbnailByPrinterId}
                    onPause={handlePauseJob}
                    onResume={handleResumeJob}
                    onCancel={handleCancelJob}
                    onAbortPrint={handleAbortPrint}
                    onPriority={handlePriorityChange}
                    onDispatch={handleDispatchJob}
                    onSchedule={(jobId) => setScheduleModalJobId(jobId)}
                    onEdit={(jobId) => {
                      setSelectedJobId(jobId);
                      setIsJobDetailsModalOpen(true);
                    }}
                  />
                ) : loading ? (
                  <div className="flex justify-center items-center py-12 bg-pf-bg-1 border border-pf-border rounded-lg">
                    <div className="text-pf-text-secondary">Loading jobs...</div>
                  </div>
                ) : jobs.length === 0 ? (
                  <QueueJobsTable jobs={[]} />
                ) : queueViewMode === "list" ? (
                  <QueueJobsListView
                    jobs={jobs}
                    dispatchingJobId={dispatchingJobId}
                    cancelingJobId={cancelingJobId}
                    dispatchUploadProgressByJobId={dispatchUploadProgressByJobId}
                    printProgressByPrinterId={printProgressByPrinterId}
                    printThumbnailByPrinterId={printThumbnailByPrinterId}
                    onPause={handlePauseJob}
                    onResume={handleResumeJob}
                    onCancel={handleCancelJob}
                    onAbortPrint={handleAbortPrint}
                    onPriority={handlePriorityChange}
                    onDispatch={handleDispatchJob}
                    onSchedule={(jobId) => setScheduleModalJobId(jobId)}
                    onEdit={(jobId) => {
                      setSelectedJobId(jobId);
                      setIsJobDetailsModalOpen(true);
                    }}
                  />
                ) : (
                  <QueueJobsCardView
                    jobs={jobs}
                    dispatchingJobId={dispatchingJobId}
                    cancelingJobId={cancelingJobId}
                    dispatchUploadProgressByJobId={dispatchUploadProgressByJobId}
                    printProgressByPrinterId={printProgressByPrinterId}
                    printThumbnailByPrinterId={printThumbnailByPrinterId}
                    onPause={handlePauseJob}
                    onResume={handleResumeJob}
                    onCancel={handleCancelJob}
                    onAbortPrint={handleAbortPrint}
                    onPriority={handlePriorityChange}
                    onDispatch={handleDispatchJob}
                    onSchedule={(jobId) => setScheduleModalJobId(jobId)}
                    onEdit={(jobId) => {
                      setSelectedJobId(jobId);
                      setIsJobDetailsModalOpen(true);
                    }}
                  />
                )}
              </div>
            </div>
          </Tabs.Panel>

          {/* Tab 2: Timeline */}
          <Tabs.Panel id="timeline">
            <QueueTimelineTab stats={stats} dateFrom={dateRange.from} dateTo={dateRange.to} />
          </Tabs.Panel>

          {/* Tab 3: History */}
          <Tabs.Panel id="history">
            <QueueHistoryTab
              onRerun={handleRerunJob}
              onViewDetails={(jobId) => {
                setSelectedJobId(jobId);
                setIsJobDetailsModalOpen(true);
              }}
              dateFrom={dateRange.from}
              dateTo={dateRange.to}
            />
          </Tabs.Panel>

          {/* Tab 4: Dispatch Log */}
          <Tabs.Panel id="dispatch-log">
            <DispatchLogTab dateFrom={dateRange.from} dateTo={dateRange.to} />
          </Tabs.Panel>
        </Tabs.Panels>
      </Tabs>

      {/* Cancel Confirmation Modal */}
      <ConfirmationModal
        isOpen={showCancelConfirmation}
        title="Cancel Print Job"
        message="Are you sure you want to cancel this print job?"
        confirmButtonText="Cancel Job"
        cancelButtonText="Keep Job"
        isDangerous={true}
        isConfirming={cancelingJobId !== null}
        onConfirm={handleConfirmCancel}
        onCancel={() => {
          setShowCancelConfirmation(false);
          setJobToCancel(null);
        }}
      />

      {/* Mobile Fallback Modal - only show on mobile or if modal explicitly requested */}
      {isJobDetailsModalOpen && (
        <JobDetailsModal
          jobId={selectedJobId || ""}
          isOpen={isJobDetailsModalOpen}
          onClose={handleCloseJobDetailsModal}
          onSave={handleJobDetailsSaved}
        />
      )}

      {/* Schedule Modal */}
      <ScheduleModal
        isOpen={scheduleModalJobId !== null}
        onClose={() => setScheduleModalJobId(null)}
        jobId={scheduleModalJobId || undefined}
      />

      {/* Spool Validation Modal — shown before dispatch when spool issues detected */}
      <SpoolValidationModal
        isOpen={spoolValidationCtx !== null}
        onClose={() => {
          setSpoolValidationCtx(null);
          setMismatchReviewedRowVersion(null);
        }}
        onProceed={(jobId) => {
          setSpoolValidationCtx(null);
          if (!mismatchReviewedRowVersion) {
            setError("The reviewed job revision is unavailable. Review the refreshed row again.");
            return;
          }
          void executeDispatch(jobId, mismatchReviewedRowVersion);
          setMismatchReviewedRowVersion(null);
        }}
        context={spoolValidationCtx}
      />
    </PageTemplate>
  );
}
