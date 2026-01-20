import { useEffect, useState, useCallback } from "react";
import { PageTemplate } from "@/common/components/PageTemplate";
import { Alert } from "@/common/components/ui/Alert";
import { Button } from "@/common/components/ui/Button";
import { ConfirmationModal } from "@/common/components/modals/ConfirmationModal";
import { Tabs } from "@/common/components/ui/Tabs";
import { MasterDetailLayout } from "@/common/components/layout/MasterDetailLayout";
import { ArrowLeftIcon } from "@/common/components/icons/MdiIcons";
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
  const [activeTab, setActiveTab] = useState("all-jobs");
  const [isJobDetailsModalOpen, setIsJobDetailsModalOpen] = useState(false);
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null);
  const [showDetailPanel, setShowDetailPanel] = useState(false);

  // Keyboard navigation for job list (only for active tab)
  const { selectedIndex } = useKeyboardNavigation(
    jobs,
    (job: QueuedPrintJobWithFileMetaDto) => {
      setSelectedJobId(job.id);
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
          handleCancelJob(jobs[selectedIndex].id);
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
            await handlePauseJob(job.id);
          } else if (job.job.status === 'Paused') {
            await handleResumeJob(job.id);
          }
        }
      },
      description: 'Pause/resume selected job'
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

  const handleCancelJob = (jobId: string) => {
    setJobToCancel(jobId);
    setShowCancelConfirmation(true);
  };

  const handleConfirmCancel = async () => {
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
  };

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

  const handleResumeJob = async (jobId: string) => {
    try {
      await apiClient.resumeJob(jobId);
      loadJobs(true);
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to resume job";
      setError(errorMessage);
    }
  };

  const handleRerunJob = async (jobId: string) => {
    try {
      await apiClient.rerunJob(jobId);
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
          {/* Tab 1: All Jobs with Master-Detail Layout */}
          <Tabs.Panel id="all-jobs">
            <MasterDetailLayout
              master={
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
                      onPause={handlePauseJob}
                      onResume={handleResumeJob}
                      onCancel={handleCancelJob}
                      onPriority={handlePriorityChange}
                      onEdit={(jobId) => {
                        setSelectedJobId(jobId);
                        setShowDetailPanel(true);
                      }}
                    />
                  </div>
                </div>
              }
              detail={
                selectedJobId ? (
                  <div className="flex flex-col h-full">
                    {/* Detail Header with Close Button */}
                    <div className="flex items-center justify-between p-4 border-b border-pf-border">
                      <h3 className="text-lg font-semibold">Job Details</h3>
                      <Button
                        onClick={() => setShowDetailPanel(false)}
                        variant="subtle"
                        size="sm"
                        title="Close detail panel (Esc)"
                      >
                        <ArrowLeftIcon className="w-5 h-5" />
                      </Button>
                    </div>

                    {/* Job Details Content */}
                    <div className="flex-1 overflow-auto p-4">
                      <JobDetailsModal
                        jobId={selectedJobId}
                        isOpen={true}
                        onClose={() => setShowDetailPanel(false)}
                        onSave={() => {
                          setShowDetailPanel(false);
                          setSelectedJobId(null);
                          loadJobs(true);
                        }}
                      />
                    </div>
                  </div>
                ) : (
                  <div className="flex items-center justify-center h-full text-pf-text-secondary">
                    <p>Select a job to view details</p>
                  </div>
                )
              }
              hasDetail={!!selectedJobId}
              onCloseDetail={() => setShowDetailPanel(false)}
            />
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
                // TODO: Navigate to job details page
              if (window.PrintFarmerDebug?.utilities) console.log("View job details:", jobId);
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
