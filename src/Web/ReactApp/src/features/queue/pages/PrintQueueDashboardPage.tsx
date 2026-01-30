import { useEffect, useState, useCallback } from "react";
import { PageTemplate } from "@/common/components/PageTemplate";
import { Alert } from "@/common/components/ui/Alert";
import { ConfirmationModal } from "@/common/components/modals/ConfirmationModal";
import { Tabs } from "@/common/components/ui/Tabs";
import { useKeyboardNavigation } from "@/common/hooks/useKeyboardNavigation";
import { useKeyboardShortcuts } from "@/common/hooks/useKeyboardShortcuts";
import { TableFiltersBar } from "../components/QueueFiltersBar";
import { QueueJobsTable } from "../components/QueueJobsTable";
import JobDetailsModal from "../components/JobDetailsModal";
import ModelFilteredJobsTab from "../components/ModelFilteredJobsTab";
import QueueHistoryTab from "../components/QueueHistoryTab";
import TimingTab from "../components/TimingTab";
import { apiClient } from "@/services/api";
import type {
  QueuedPrintJobWithFileMetaDto,
  QueueStatsDto,
} from "@/types/api";

// localStorage keys for persisting user preferences
const STORAGE_KEY_ACTIVE_TAB = 'printfarmer-queue-active-tab';
const VALID_TABS = ['all-jobs', 'by-model', 'timing', 'history'] as const;

export function PrintQueueDashboardPage() {
  const [jobs, setJobs] = useState<QueuedPrintJobWithFileMetaDto[]>([]);
  const [stats, setStats] = useState<QueueStatsDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [modelFilter, setModelFilter] = useState<string | null>(null);
  const [materialFilter, setMaterialFilter] = useState<string | null>(null);
  const [showCancelConfirmation, setShowCancelConfirmation] = useState(false);
  const [jobToCancel, setJobToCancel] = useState<string | null>(null);
  
  // Persist active tab to localStorage
  const [activeTab, setActiveTabState] = useState(() => {
    const saved = localStorage.getItem(STORAGE_KEY_ACTIVE_TAB);
    return saved && VALID_TABS.includes(saved as typeof VALID_TABS[number]) ? saved : 'all-jobs';
  });
  
  const setActiveTab = useCallback((tab: string) => {
    setActiveTabState(tab);
    localStorage.setItem(STORAGE_KEY_ACTIVE_TAB, tab);
  }, []);
  
  const [isJobDetailsModalOpen, setIsJobDetailsModalOpen] = useState(false);
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null);
  const [showDetailPanel, setShowDetailPanel] = useState(false);
  const [dispatchingJobId, setDispatchingJobId] = useState<string | null>(null);

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

  const loadJobs = useCallback(async (isBackgroundRefresh = false) => {
    try {
      setError(null);
      
      // Only show full loading state on initial load, not background refreshes
      if (!isBackgroundRefresh) {
        setLoading(true);
      } else {
        setIsRefreshing(true);
      }

      // Fetch both in parallel to reduce flashing
      const [data, queueStats] = await Promise.all([
        apiClient.getAnalyticsQueueJobs(
          statusFilter || undefined,
          modelFilter || undefined,
          materialFilter || undefined,
          100,
          0
        ),
        apiClient.getAnalyticsQueueStats()
      ]);

      setJobs(data as QueuedPrintJobWithFileMetaDto[]);
      setStats(queueStats as QueueStatsDto);

      setLoading(false);
      setIsRefreshing(false);
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to load jobs";
      setError(errorMessage);
      setLoading(false);
      setIsRefreshing(false);
    }
  }, [statusFilter, modelFilter, materialFilter]);

  // Initial load and filter changes trigger immediate fetch
  useEffect(() => {
    loadJobs(false);
  }, [loadJobs]);

  // Background refresh interval - separate effect to avoid recreating interval on filter changes
  useEffect(() => {
    const interval = setInterval(() => loadJobs(true), 10000);
    return () => clearInterval(interval);
  }, [loadJobs]);

  const handleCancelJob = useCallback((jobId: string) => {
    setJobToCancel(jobId);
    setShowCancelConfirmation(true);
  }, []);

  const handleConfirmCancel = useCallback(async () => {
    if (!jobToCancel) return;

    try {
      await apiClient.cancelPrintQueueJob(jobToCancel);
      setShowCancelConfirmation(false);
      setJobToCancel(null);
      loadJobs(true);
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to cancel job";
      setError(errorMessage);
      setShowCancelConfirmation(false);
      setJobToCancel(null);
    }
  }, [jobToCancel, loadJobs]);

  const handlePauseJob = async (jobId: string) => {
    try {
      await apiClient.pauseJob(jobId);
      loadJobs(true);
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to pause job";
      setError(errorMessage);
    }
  };

  const handleResumeJob = useCallback(async (jobId: string) => {
    try {
      await apiClient.resumeJob(jobId);
      loadJobs(true);
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to resume job";
      setError(errorMessage);
    }
  }, [loadJobs]);

  const handleDispatchJob = useCallback(async (jobId: string) => {
    setDispatchingJobId(jobId);
    try {
      await apiClient.dispatchPrintQueueJob(jobId);
      loadJobs(true);
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to start print job";
      setError(errorMessage);
    } finally {
      setDispatchingJobId(null);
    }
  }, [loadJobs]);

  const handleRerunJob = async (jobId: string) => {
    try {
      await apiClient.rerunPrintQueueJob(jobId);
      setError(null);
      // Reload jobs to show the new job in the queue
      await loadJobs(true);
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to rerun job";
      setError(errorMessage);
    }
  };

  const handlePriorityChange = async (jobId: string, newPriority: number) => {
    try {
      await apiClient.updateJobPriority(jobId, newPriority);
      loadJobs(true);
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to update priority";
      setError(errorMessage);
    }
  };

  const handleCloseJobDetailsModal = () => {
    setIsJobDetailsModalOpen(false);
    setSelectedJobId(null);
  };

  const handleJobDetailsSaved = () => {
    handleCloseJobDetailsModal();
    loadJobs(true);
  };

  return (
    <PageTemplate
      title="Print Queue Dashboard"
      subtitle="View and manage all queued and printing jobs"
    >
      {error && (
        <Alert type="error" className="mb-4">
          {error}
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
          <Tabs.Tab id="all-jobs">All Jobs</Tabs.Tab>
          <Tabs.Tab id="by-model">By Model</Tabs.Tab>
          <Tabs.Tab id="timing">Timing & Analytics</Tabs.Tab>
          <Tabs.Tab id="history">History</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panels>
          {/* Tab 1: All Jobs */}
          <Tabs.Panel id="all-jobs">
            <div className="flex flex-col h-full w-full min-h-0">
              {/* Filters */}
              <div className="flex-shrink-0 p-4 border-b border-pf-border bg-pf-bg-1">
                <TableFiltersBar
                  onStatusChange={setStatusFilter}
                  onModelChange={setModelFilter}
                  onMaterialChange={setMaterialFilter}
                  onRefresh={() => loadJobs(false)}
                  isLoading={loading || isRefreshing}
                />
              </div>

              {/* Jobs Table */}
              <div className="flex-1 overflow-auto bg-pf-bg-1 p-4 min-h-0">
                <QueueJobsTable
                  jobs={jobs}
                  isLoading={loading}
                  dispatchingJobId={dispatchingJobId}
                  onPause={handlePauseJob}
                  onResume={handleResumeJob}
                  onCancel={handleCancelJob}
                  onPriority={handlePriorityChange}
                  onDispatch={handleDispatchJob}
                  onEdit={(jobId) => {
                    setSelectedJobId(jobId);
                    setIsJobDetailsModalOpen(true);
                  }}
                />
              </div>
            </div>
          </Tabs.Panel>

          {/* Tab 2: By Model */}
          <Tabs.Panel id="by-model">
            <ModelFilteredJobsTab
              onViewAllJobs={(modelName) => {
                setModelFilter(modelName);
                setActiveTab("all-jobs");
              }}
              onJobAction={async (jobId, action) => {
                switch (action) {
                  case "pause":
                    await handlePauseJob(jobId);
                    break;
                  case "resume":
                    await handleResumeJob(jobId);
                    break;
                  case "cancel":
                    await handleCancelJob(jobId);
                    break;
                  case "priority":
                    // Priority change would require a UI dialog to select priority
                    // For now, we'll skip this in the model cards
                    break;
                }
              }}
            />
          </Tabs.Panel>

          {/* Tab 3: Timing & Analytics */}
          <Tabs.Panel id="timing">
            <TimingTab />
          </Tabs.Panel>

          {/* Tab 4: History */}
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
