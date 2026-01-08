import { useEffect, useState, useCallback } from "react";
import { PageTemplate } from "@/common/components/PageTemplate";
import { Alert } from "@/common/components/ui/Alert";
import { ConfirmationModal } from "@/common/components/modals/ConfirmationModal";
import { Tabs } from "@/common/components/ui/Tabs";
import { TableFiltersBar } from "../components/QueueFiltersBar";
import { QueueJobsTable } from "../components/QueueJobsTable";
import JobDetailsModal from "../components/JobDetailsModal";
import ModelFilteredJobsTab from "../components/ModelFilteredJobsTab";
import QueueHistoryTab from "../components/QueueHistoryTab";
import {
  printQueueService,
  QueuedPrintJobWithFileMetaDto,
  QueueStatsDto,
} from "@/services/printQueueService";

export function PrintQueueDashboardPage() {
  const [jobs, setJobs] = useState<QueuedPrintJobWithFileMetaDto[]>([]);
  const [stats, setStats] = useState<QueueStatsDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [modelFilter, setModelFilter] = useState<string | null>(null);
  const [materialFilter, setMaterialFilter] = useState<string | null>(null);
  const [showCancelConfirmation, setShowCancelConfirmation] = useState(false);
  const [jobToCancel, setJobToCancel] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState("all-jobs");
  const [isJobDetailsModalOpen, setIsJobDetailsModalOpen] = useState(false);
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null);

  const loadJobs = useCallback(async () => {
    try {
      setError(null);
      setLoading(true);
      const data = await printQueueService.getAllQueuedJobsAsync(
        statusFilter || undefined,
        modelFilter || undefined,
        materialFilter || undefined,
        100,
        0
      );
      setJobs(data);

      // Also load stats
      const queueStats = await printQueueService.getQueueStatsAsync();
      setStats(queueStats);

      setLoading(false);
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to load jobs";
      setError(errorMessage);
      setLoading(false);
    }
  }, [statusFilter, modelFilter, materialFilter]);

  useEffect(() => {
    loadJobs();
    const interval = setInterval(loadJobs, 10000); // Refresh every 10 seconds
    return () => clearInterval(interval);
  }, [loadJobs]);

  const handleCancelJob = (jobId: string) => {
    setJobToCancel(jobId);
    setShowCancelConfirmation(true);
  };

  const handleConfirmCancel = async () => {
    if (!jobToCancel) return;

    try {
      await printQueueService.cancelJobAsync(jobToCancel);
      setShowCancelConfirmation(false);
      setJobToCancel(null);
      loadJobs();
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
      await printQueueService.pauseJobAsync(jobId);
      loadJobs();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to pause job";
      setError(errorMessage);
    }
  };

  const handleResumeJob = async (jobId: string) => {
    try {
      await printQueueService.resumeJobAsync(jobId);
      loadJobs();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to resume job";
      setError(errorMessage);
    }
  };

  const handleRerunJob = async (jobId: string) => {
    try {
      await printQueueService.rerunJobAsync(jobId);
      setError(null);
      // Reload jobs to show the new job in the queue
      await loadJobs();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to rerun job";
      setError(errorMessage);
    }
  };

  const handlePriorityChange = async (jobId: string, newPriority: number) => {
    try {
      await printQueueService.updateJobPriorityAsync(jobId, newPriority);
      loadJobs();
    } catch (err) {
      const errorMessage =
        err instanceof Error ? err.message : "Failed to update priority";
      setError(errorMessage);
    }
  };

  const handleEditJob = (jobId: string) => {
    setSelectedJobId(jobId);
    setIsJobDetailsModalOpen(true);
  };

  const handleCloseJobDetailsModal = () => {
    setIsJobDetailsModalOpen(false);
    setSelectedJobId(null);
  };

  const handleJobDetailsSaved = () => {
    handleCloseJobDetailsModal();
    loadJobs();
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
          <Tabs.Tab id="history">History</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panels>
          {/* Tab 1: All Jobs */}
          <Tabs.Panel id="all-jobs">
            {/* Filters */}
            <TableFiltersBar
              onStatusChange={setStatusFilter}
              onModelChange={setModelFilter}
              onMaterialChange={setMaterialFilter}
              onRefresh={loadJobs}
              isLoading={loading}
            />

            {/* Jobs Table */}
            <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4 mt-4">
              <QueueJobsTable
                jobs={jobs}
                isLoading={loading}
                onPause={handlePauseJob}
                onResume={handleResumeJob}
                onCancel={handleCancelJob}
                onPriority={handlePriorityChange}
                onEdit={handleEditJob}
              />
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

          {/* Tab 3: History */}
          <Tabs.Panel id="history">
            <QueueHistoryTab
              onRerun={handleRerunJob}
              onViewDetails={(jobId) => {
                // TODO: Navigate to job details page
                console.log("View job details:", jobId);
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

      {/* Job Details Modal */}
      <JobDetailsModal
        jobId={selectedJobId || ""}
        isOpen={isJobDetailsModalOpen}
        onClose={handleCloseJobDetailsModal}
        onSave={handleJobDetailsSaved}
        onRefresh={loadJobs}
      />
    </PageTemplate>
  );
}
