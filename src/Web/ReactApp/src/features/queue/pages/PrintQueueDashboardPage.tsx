import { useEffect, useState, useCallback } from "react";
import { useSearchParams, useParams, useNavigate } from "react-router";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { PageTemplate } from "@/common/components/PageTemplate";
import { Alert } from "@/common/components/ui/Alert";
import { Toggle } from "@/common/components/ui/Toggle";
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
import { validateSpoolForDispatch } from "../utils/spoolValidation";
import type { SpoolValidationContext } from "../utils/spoolValidation";
import { ScheduleModal } from "@/features/scheduling/components/ScheduleModal";
import { apiClient } from "@/services/api";
import { printerSignalRService } from "@/services/printer-signalr";
import { usePageTour } from "@/common/hooks/usePageTour";
import { printQueueTour } from "@/features/queue/tours/print-queue.tour";
import { HelpButton } from "@/common/components/HelpButton";
import { mergePrinterProgress, mergePrinterThumbnail } from "@/features/queue/utils/printerProgress";
import type { DispatchUploadProgressDto } from "@/types/api";
import type {
  QueuedPrintJobWithFileMetaDto,
  QueueStatsDto,
} from "@/types/api";

// localStorage keys for persisting user preferences
const STORAGE_KEY_ACTIVE_TAB = 'printfarmer-queue-active-tab';
const STORAGE_KEY_QUEUE_VIEW_MODE = 'printfarmer-queue-view-mode';
const VALID_TABS = ['print-queue', 'timeline', 'history', 'dispatch-log'] as const;
const VALID_QUEUE_VIEW_MODES: QueueViewMode[] = ["table", "list", "cards"];

const DISPATCH_SETTINGS_KEY = ['dispatch-settings'] as const;

interface DispatchSettingsResponse {
  autoDispatchEnabled: boolean;
  autoDispatchMode: string;
  idleThresholdSeconds: number;
  minimumScoreThreshold: number;
  maxConcurrentDispatches: number;
  loadBalancingStrategy: string;
  updatedAt: string;
}

function AutoDispatchGlobalToggle() {
  const queryClient = useQueryClient();

  const { data: settings, isError } = useQuery<DispatchSettingsResponse>({
    queryKey: DISPATCH_SETTINGS_KEY,
    queryFn: async () => {
      const res = await apiClient.get<DispatchSettingsResponse>('/dispatch-settings');
      return res.data;
    },
    staleTime: 30_000,
  });

  const toggleMutation = useMutation({
    mutationFn: async (enabled: boolean) => {
      if (!settings) return;
      const res = await apiClient.put<DispatchSettingsResponse>('/dispatch-settings', {
        ...settings,
        autoDispatchEnabled: enabled,
        autoDispatchMode: enabled ? 'Auto' : 'Manual',
      });
      return res.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: DISPATCH_SETTINGS_KEY });
    },
  });

  const isEnabled = settings?.autoDispatchEnabled ?? false;

  const handleToggle = async () => {
    const newEnabled = !isEnabled;
    try {
      await toggleMutation.mutateAsync(newEnabled);
      toast.success(newEnabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled');
    } catch {
      toast.error('Failed to update auto-dispatch');
    }
  };

  if (isError) {
    return (
      <div className="flex items-center gap-2 shrink-0">
        <Toggle
          checked={false}
          onChange={() => {}}
          disabled
          size="sm"
          aria-label="Auto-dispatch unavailable"
        />
        <span className="text-xs text-pf-text-secondary">Auto-dispatch unavailable</span>
      </div>
    );
  }

  return (
    <div className="flex items-center gap-2 shrink-0">
      <Toggle
        checked={isEnabled}
        onChange={handleToggle}
        disabled={toggleMutation.isPending || !settings}
        size="sm"
        aria-label="Toggle system auto-dispatch"
      />
      <span className="text-xs font-medium text-pf-text-primary">
        Auto-dispatch
      </span>
    </div>
  );
}

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
  const [jobToCancel, setJobToCancel] = useState<string | null>(null);
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
  const [printProgressByPrinterId, setPrintProgressByPrinterId] = useState<Record<string, number>>({});
  const [printThumbnailByPrinterId, setPrintThumbnailByPrinterId] = useState<Record<string, string>>({});
  const [scheduleModalJobId, setScheduleModalJobId] = useState<string | null>(null);
  const [spoolValidationCtx, setSpoolValidationCtx] = useState<SpoolValidationContext | null>(null);

  const { data: jobs = [], isLoading: loading, isFetching: isRefreshing, error: jobsError } = useQuery({
    queryKey: ['queue-jobs', statusFilter, modelFilter, materialFilter, sortBy, dateRange.from?.toISOString(), dateRange.to?.toISOString()],
    queryFn: () => apiClient.getAnalyticsQueueJobs(
      statusFilter || undefined,
      modelFilter || undefined,
      materialFilter || undefined,
      sortBy,
      100,
      0,
      dateRange.from ?? undefined,
      dateRange.to ?? undefined,
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

  const invalidateQueue = useCallback(() => {
    setError(null);
    queryClient.invalidateQueries({ queryKey: ['queue-jobs'] });
    queryClient.invalidateQueries({ queryKey: ['queue-stats'] });
  }, [queryClient]);

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
    setJobToCancel(jobId);
    setShowCancelConfirmation(true);
  }, []);

  const handleConfirmCancel = useCallback(async () => {
    if (!jobToCancel) return;

    try {
      setCancelingJobId(jobToCancel);
      await apiClient.cancelPrintQueueJob(jobToCancel);
      setShowCancelConfirmation(false);
      setJobToCancel(null);
      invalidateQueue();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to cancel job";
      setError(errorMessage);
      setShowCancelConfirmation(false);
      setJobToCancel(null);
    } finally {
      setCancelingJobId(null);
    }
  }, [jobToCancel, invalidateQueue]);

  const handlePauseJob = async (jobId: string) => {
    try {
      await apiClient.pauseJob(jobId);
      invalidateQueue();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to pause job";
      setError(errorMessage);
    }
  };

  const handleResumeJob = useCallback(async (jobId: string) => {
    try {
      await apiClient.resumeJob(jobId);
      invalidateQueue();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to resume job";
      setError(errorMessage);
    }
  }, [invalidateQueue]);

  const handleAbortPrint = useCallback(async (jobId: string) => {
    try {
      await apiClient.abortPrint(jobId);
      invalidateQueue();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to abort print";
      setError(errorMessage);
    }
  }, [invalidateQueue]);

  /** Actually send the dispatch request for a job (no spool check). */
  const executeDispatch = useCallback(async (jobId: string) => {
    setDispatchingJobId(jobId);
    setDispatchUploadProgressByJobId((prev) => {
      const copy = { ...prev };
      delete copy[jobId];
      return copy;
    });
    try {
      await apiClient.dispatchPrintQueueJob(jobId);
      invalidateQueue();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to start print job";
      setError(errorMessage);
    } finally {
      setDispatchingJobId(null);
      setDispatchUploadProgressByJobId((prev) => {
        const copy = { ...prev };
        delete copy[jobId];
        return copy;
      });
    }
  }, [invalidateQueue]);

  /** Validate spool state before dispatching; shows modal if issues found. */
  const handleDispatchJob = useCallback(async (jobId: string) => {
    const jobWrapper = jobs.find(j => j.job.id === jobId);
    if (!jobWrapper?.assignedPrinter) {
      await executeDispatch(jobId);
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
        setDispatchingJobId(null);
      } else {
        // No issues — dispatch directly
        setDispatchingJobId(null);
        await executeDispatch(jobId);
      }
    } catch {
      // Validation fetch failed — don't block dispatch
      setDispatchingJobId(null);
      await executeDispatch(jobId);
    }
  }, [jobs, executeDispatch]);

  // Dispatch upload progress subscription (SignalR)
  useEffect(() => {
    printerSignalRService.connect();
    const unsub = printerSignalRService.onDispatchUploadProgress((progress) => {
      setDispatchUploadProgressByJobId((prev) => ({
        ...prev,
        [progress.jobId]: progress,
      }));
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
    try {
      await apiClient.rerunPrintQueueJob(jobId);
      setError(null);
      invalidateQueue();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to rerun job";
      setError(errorMessage);
    }
  };

  const handlePriorityChange = async (jobId: string, newPriority: number) => {
    try {
      await apiClient.updateJobPriority(jobId, newPriority);
      invalidateQueue();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to update priority";
      setError(errorMessage);
    }
  };

  const handleReorder = async (moves: { jobId: string; newPosition: number }[]) => {
    try {
      await apiClient.reorderQueueJobs(moves);
      invalidateQueue();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to reorder jobs";
      setError(errorMessage);
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

      <QueueDateRangeBar
        dateFrom={dateRange.from}
        dateTo={dateRange.to}
        onChange={setDateRange}
      />

      {/* Tabbed Interface */}
      <Tabs activeTab={activeTab} onTabChange={setActiveTab}>
        <div data-tour="queue-tabs">
          <Tabs.List>
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
                    onReorder={handleReorder}
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
        onClose={() => setSpoolValidationCtx(null)}
        onProceed={(jobId) => {
          setSpoolValidationCtx(null);
          executeDispatch(jobId);
        }}
        context={spoolValidationCtx}
      />
    </PageTemplate>
  );
}
