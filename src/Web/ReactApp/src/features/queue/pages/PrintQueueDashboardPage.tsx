import { useEffect, useState, useCallback } from "react";
import { useSearchParams, useParams, useNavigate } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
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
import JobDetailsModal from "../components/JobDetailsModal";
import QueueHistoryTab from "../components/QueueHistoryTab";
import { useAllAutoPrintStatuses, useSetAllAutoPrintEnabled } from "@/features/printers/hooks/useAutoPrint";
import { apiClient } from "@/services/api";
import { printerSignalRService } from "@/services/printer-signalr";
import type { DispatchUploadProgressDto } from "@/types/api";
import type {
  QueuedPrintJobWithFileMetaDto,
  QueueStatsDto,
} from "@/types/api";

// localStorage keys for persisting user preferences
const STORAGE_KEY_ACTIVE_TAB = 'printfarmer-queue-active-tab';
const VALID_TABS = ['print-queue', 'history'] as const;

function AutoDispatchGlobalToggle() {
  const { data: statuses, isError } = useAllAutoPrintStatuses();
  const setAllEnabled = useSetAllAutoPrintEnabled();

  const totalPrinters = statuses?.length ?? 0;
  const enabledCount = statuses?.filter(s => s.autoPrintEnabled).length ?? 0;
  const allEnabled = totalPrinters > 0 && enabledCount === totalPrinters;
  const isIndeterminate = enabledCount > 0 && enabledCount < totalPrinters;

  const handleToggle = async () => {
    const newEnabled = !allEnabled;
    try {
      await setAllEnabled.mutateAsync(newEnabled);
      toast.success(newEnabled
        ? `Auto-dispatch enabled for all ${totalPrinters} printers`
        : 'Auto-dispatch disabled for all printers');
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

  if (totalPrinters === 0) return null;

  return (
    <div className="flex items-center gap-2 shrink-0">
      <Toggle
        checked={allEnabled}
        onChange={handleToggle}
        disabled={setAllEnabled.isPending}
        size="sm"
        aria-label="Toggle auto-dispatch for all printers"
      />
      <div className="flex flex-col">
        <span className="text-xs font-medium text-pf-text-primary">
          Auto-dispatch{isIndeterminate && ' (partial)'}
        </span>
        <span className="text-xs text-pf-text-secondary">
          {enabledCount}/{totalPrinters} printers
        </span>
      </div>
    </div>
  );
}

export function PrintQueueDashboardPage() {
  const queryClient = useQueryClient();
  const [searchParams] = useSearchParams();
  const { tabId } = useParams<{ tabId?: string }>();
  const navigate = useNavigate();
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [modelFilter, setModelFilter] = useState<string | null>(null);
  const [materialFilter, setMaterialFilter] = useState<string | null>(null);
  const [showCancelConfirmation, setShowCancelConfirmation] = useState(false);
  const [jobToCancel, setJobToCancel] = useState<string | null>(null);
  const [cancelingJobId, setCancelingJobId] = useState<string | null>(null);
  
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
  
  const [isJobDetailsModalOpen, setIsJobDetailsModalOpen] = useState(false);
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null);
  const [showDetailPanel, setShowDetailPanel] = useState(false);
  const [dispatchingJobId, setDispatchingJobId] = useState<string | null>(null);
  const [dispatchUploadProgressByJobId, setDispatchUploadProgressByJobId] = useState<
    Record<string, DispatchUploadProgressDto>
  >({});

  const { data: jobs = [], isLoading: loading, isFetching: isRefreshing, error: jobsError } = useQuery({
    queryKey: ['queue-jobs', statusFilter, modelFilter, materialFilter],
    queryFn: () => apiClient.getAnalyticsQueueJobs(
      statusFilter || undefined,
      modelFilter || undefined,
      materialFilter || undefined,
      100,
      0
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

  const handleDispatchJob = useCallback(async (jobId: string) => {
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
    >
      {displayError && (
        <Alert type="error" className="mb-4">
          {displayError}
        </Alert>
      )}

      {/* Stats Summary */}
      {stats && (
        <div className="grid grid-cols-1 md:grid-cols-4 gap-4 mb-6">
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

      {/* Tabbed Interface */}
      <Tabs activeTab={activeTab} onTabChange={setActiveTab}>
        <Tabs.List>
          <Tabs.Tab id="print-queue">Print Queue</Tabs.Tab>
          <Tabs.Tab id="history">History</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panels>
          {/* Tab 1: Queue */}
          <Tabs.Panel id="print-queue">
            <div className="flex flex-col h-full w-full min-h-0">
              {/* Filters + Auto-dispatch global toggle */}
              <div className="shrink-0 p-4 border-b border-pf-border bg-pf-bg-1">
                <div className="flex items-center justify-between gap-4 flex-wrap">
                  <div className="flex-1 min-w-0">
                    <TableFiltersBar
                      onStatusChange={setStatusFilter}
                      onModelChange={setModelFilter}
                      onMaterialChange={setMaterialFilter}
                      onRefresh={invalidateQueue}
                      isLoading={loading || isRefreshing}
                    />
                  </div>
                  <AutoDispatchGlobalToggle />
                </div>
              </div>

              {/* Jobs Table */}
              <div className="flex-1 overflow-auto bg-pf-bg-1 p-4 min-h-0">
                <QueueJobsTable
                  jobs={jobs}
                  isLoading={loading}
                  dispatchingJobId={dispatchingJobId}
                  cancelingJobId={cancelingJobId}
                  dispatchUploadProgressByJobId={dispatchUploadProgressByJobId}
                  onPause={handlePauseJob}
                  onResume={handleResumeJob}
                  onCancel={handleCancelJob}
                  onAbortPrint={handleAbortPrint}
                  onPriority={handlePriorityChange}
                  onDispatch={handleDispatchJob}
                  onReorder={handleReorder}
                  onEdit={(jobId) => {
                    setSelectedJobId(jobId);
                    setIsJobDetailsModalOpen(true);
                  }}
                />
              </div>
            </div>
          </Tabs.Panel>

          {/* Tab 2: History */}
          <Tabs.Panel id="history">
            <QueueHistoryTab
              onRerun={handleRerunJob}
              onViewDetails={(jobId) => {
                setSelectedJobId(jobId);
                setIsJobDetailsModalOpen(true);
              }}
            />
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
    </PageTemplate>
  );
}
