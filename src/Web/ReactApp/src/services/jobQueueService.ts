import { apiClient } from '@/services/api';
import type {
  JobQueuePrintJob,
  QueuedPrintJobWithFileMetaDto,
  QueueHistoryPageDto,
  QueueOverviewDto,
} from '@/types/api';

/**
 * Job queue service — queue management, dispatch, analytics, and history.
 * Delegates to the apiClient singleton which handles auth, correlation IDs,
 * and error handling automatically.
 */
export const jobQueueService = {
  // ── Queue Overview & Listing ──────────────────────────────────────────

  async getQueueOverview(
    model?: string,
    nozzle?: number,
    material?: string
  ): Promise<QueueOverviewDto[]> {
    return apiClient.getQueueOverview(model, nozzle, material);
  },

  async getJobQueue(printerId?: string): Promise<QueuedPrintJobWithFileMetaDto[]> {
    return apiClient.getJobQueue(printerId);
  },

  // ── Queue Operations ──────────────────────────────────────────────────

  async queuePrintJob(
    printerId: string,
    gcodeFileId: string,
    priority?: number
  ): Promise<JobQueuePrintJob> {
    return apiClient.queuePrintJob(printerId, gcodeFileId, priority);
  },

  async enqueueJob(request: unknown): Promise<unknown> {
    return apiClient.enqueueJob(request);
  },

  async deletePrintQueueJob(jobId: string): Promise<void> {
    return apiClient.deletePrintQueueJob(jobId);
  },

  async dispatchPrintQueueJob(jobId: string): Promise<QueuedPrintJobWithFileMetaDto> {
    return apiClient.dispatchPrintQueueJob(jobId);
  },

  // ── Job State Control ─────────────────────────────────────────────────

  async pauseJob(jobId: string): Promise<unknown> {
    return apiClient.pauseJob(jobId);
  },

  async resumeJob(jobId: string): Promise<unknown> {
    return apiClient.resumeJob(jobId);
  },

  async cancelPrintQueueJob(jobId: string): Promise<void> {
    return apiClient.cancelPrintQueueJob(jobId);
  },

  async abortPrint(jobId: string): Promise<void> {
    return apiClient.abortPrint(jobId);
  },

  async rerunPrintQueueJob(jobId: string): Promise<unknown> {
    return apiClient.rerunPrintQueueJob(jobId);
  },

  // ── Job Updates ───────────────────────────────────────────────────────

  async updateJob(jobId: string, request: unknown): Promise<unknown> {
    return apiClient.updateJob(jobId, request);
  },

  async updateJobPriority(jobId: string, newPriority: number): Promise<unknown> {
    return apiClient.updateJobPriority(jobId, newPriority);
  },

  async updateJobDetails(jobId: string, updates: unknown): Promise<unknown> {
    return apiClient.updateJobDetails(jobId, updates);
  },

  async updateJobNotes(jobId: string, notes: string): Promise<void> {
    return apiClient.updateJobNotes(jobId, notes);
  },

  // ── Bulk Operations ───────────────────────────────────────────────────

  async bulkCancelJobs(request: unknown): Promise<unknown> {
    return apiClient.bulkCancelJobs(request);
  },

  async reorderQueueJobs(
    moves: { jobId: string; newPosition: number }[]
  ): Promise<unknown> {
    return apiClient.reorderQueueJobs(moves);
  },

  // ── History & Seeding ─────────────────────────────────────────────────

  async seedHistory(printerIds?: string[]): Promise<void> {
    return apiClient.seedHistory(printerIds);
  },

  // ── Analytics ─────────────────────────────────────────────────────────

  async getAnalyticsQueueJobs(
    filterStatus?: string,
    filterModel?: string,
    filterMaterial?: string,
    sortBy?: "priority" | "deadline" | "deadline_desc",
    limit?: number,
    offset?: number
  ): Promise<unknown[]> {
    return apiClient.getAnalyticsQueueJobs(filterStatus, filterModel, filterMaterial, sortBy, limit, offset);
  },

  async getAnalyticsPrinterQueue(printerId: string, limit?: number): Promise<unknown[]> {
    return apiClient.getAnalyticsPrinterQueue(printerId, limit);
  },

  async getAnalyticsQueueStats(): Promise<unknown> {
    return apiClient.getAnalyticsQueueStats();
  },

  async getAnalyticsQueueModelStats(): Promise<unknown[]> {
    return apiClient.getAnalyticsQueueModelStats();
  },

  async getAnalyticsQueueRecommendations(limit?: number): Promise<unknown[]> {
    return apiClient.getAnalyticsQueueRecommendations(limit);
  },

  async getAnalyticsQueueHistory(
    limit?: number,
    offset?: number,
    sortBy?: string,
    statuses?: string[],
    dateStart?: string | null,
    dateEnd?: string | null
  ): Promise<QueueHistoryPageDto> {
    return apiClient.getAnalyticsQueueHistory(limit, offset, sortBy, statuses, dateStart, dateEnd);
  },

  async getAnalyticsJobDetails(jobId: string): Promise<unknown> {
    return apiClient.getAnalyticsJobDetails(jobId);
  },

  async getAnalyticsJobStateHistory(jobId: string): Promise<unknown> {
    return apiClient.getAnalyticsJobStateHistory(jobId);
  },

  async getAnalyticsTimeline(
    dateFrom?: Date,
    dateTo?: Date,
    printerId?: string,
    filterStatus?: string,
    limit?: number
  ): Promise<unknown[]> {
    return apiClient.getAnalyticsTimeline(dateFrom, dateTo, printerId, filterStatus, limit);
  },

  async getAnalyticsDurationAnalytics(
    printerId?: string,
    dateFrom?: Date,
    dateTo?: Date
  ): Promise<unknown> {
    return apiClient.getAnalyticsDurationAnalytics(printerId, dateFrom, dateTo);
  },
};
