import { apiClient } from "@/services/api";
import type { AxiosError } from "axios";

// ============= TYPES =============

export interface QueuedPrintJobDto {
  id: string;
  name: string;
  gcodeFileId: string;
  assignedPrinterId?: string;
  status: string;
  priority: number;
  queuePosition: number;
  requiredNozzleDiameter?: number;
  requiredMaterialType?: string;
  requiredCapabilities?: string[];
  estimatedPrintTimeSeconds?: number;
  estimatedFilamentUsageGrams?: number;
  actualStartTimeUtc?: string;
  actualEndTimeUtc?: string;
  actualPrintTimeSeconds?: number;
  actualFilamentUsageGrams?: number;
  failureReason?: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  queuedAtUtc: string;
}

export interface QueueGcodeFileMetaDto {
  id: string;
  fileName: string;
  fileSizeBytes: number;
  materialType?: string;
  nozzleDiameter?: number;
  estimatedPrintTimeSeconds?: number;
  estimatedFilamentUsageGrams?: number;
  createdAtUtc: string;
}

export interface QueuePrinterMetaDto {
  id: string;
  name: string;
  modelName: string;
  status: string;
  isOnline: boolean;
}

export interface QueuedPrintJobWithFileMetaDto {
  id: string;
  job: QueuedPrintJobDto;
  fileMetadata?: QueueGcodeFileMetaDto;
  printerMetadata?: QueuePrinterMetaDto;
}

export interface QueueStatsDto {
  totalQueued: number;
  totalPrinting: number;
  totalPaused: number;
  averageWaitTimeMinutes: number;
  byModel: Record<string, QueuePrinterModelStatsDto>;
}

export interface QueuePrinterModelStatsDto {
  modelName: string;
  totalQueued: number;
  currentlyPrinting: number;
  oldestQueuedAtUtc?: string;
  averageQueueWaitMinutes: number;
}

export interface QueueHistoryPageDto {
  entries: QueueHistoryEntryDto[];
  totalCount: number;
  currentPage: number;
  pageSize: number;
}

export interface QueueHistoryEntryDto {
  id: string;
  jobName: string;
  printerName: string;
  status: string;
  completionPercentage: number;
  startedAtUtc: string;
  completedAtUtc?: string;
  actualPrintTimeSeconds: number;
  failureReason?: string;
}

// ============= TIMING & ANALYTICS TYPES =============

export interface TimelineEventDto {
  jobId: string;
  jobName: string;
  printerName: string;
  state: string;
  enteredAtUtc: string;
  exitedAtUtc?: string;
  durationSeconds?: number;
  estimatedDurationSeconds?: number;
  variancePercent?: number;
}

export interface StateTransitionDto {
  fromState: string;
  toState: string;
  transitionedAtUtc: string;
  durationInStateSeconds?: number;
  notes?: string;
}

export interface JobStateHistoryDto {
  jobId: string;
  jobName: string;
  transitions: StateTransitionDto[];
  totalDurationSeconds?: number;
  estimatedDurationSeconds?: number;
  variancePercent?: number;
}

export interface DurationStatsDto {
  printerId: string;
  printerName: string;
  totalJobs: number;
  averageEstimatedSeconds?: number;
  averageActualSeconds?: number;
  accuracyPercent?: number;
  variancePercent?: number;
  minActualSeconds?: number;
  maxActualSeconds?: number;
}

export interface DurationAnalyticsDto {
  totalJobs: number;
  averageEstimatedSeconds?: number;
  averageActualSeconds?: number;
  overallAccuracyPercent?: number;
  overallVariancePercent?: number;
  byPrinter: Record<string, DurationStatsDto>;
  topPerformers: DurationStatsDto[];
  needsAttention: DurationStatsDto[];
}

export interface EnqueueQueueJobRequest {
  gcodeFileId: string;
  priority?: number;
  assignedPrinterId?: string;
  requiredNozzleDiameter?: number;
  requiredMaterialType?: string;
}

export interface UpdateQueueJobRequest {
  priority?: number;
  assignedPrinterId?: string;
  status?: string;
  failureReason?: string;
}

export interface BulkCancelQueueJobsRequest {
  jobIds: string[];
}

export interface QueueBulkOperationResultDto {
  totalRequested: number;
  successfulCount: number;
  failedCount: number;
  failures: QueueOperationFailureDto[];
  completedAtUtc: string;
}

export interface QueueOperationFailureDto {
  itemId: string;
  errorMessage: string;
  errorCode?: string;
}

// ============= SERVICE =============

class PrintQueueService {
  private baseUrl = "printQueue";

  /**
   * Get all queued and printing jobs with optional filtering
   */
  async getAllQueuedJobsAsync(
    filterStatus?: string,
    filterModel?: string,
    filterMaterial?: string,
    limit: number = 50,
    offset: number = 0
  ): Promise<QueuedPrintJobWithFileMetaDto[]> {
    try {
      const params = new URLSearchParams();
      if (filterStatus) params.append("filterStatus", filterStatus);
      if (filterModel) params.append("filterModel", filterModel);
      if (filterMaterial) params.append("filterMaterial", filterMaterial);
      params.append("limit", limit.toString());
      params.append("offset", offset.toString());

      const response = await apiClient.get<QueuedPrintJobWithFileMetaDto[]>(
        `${this.baseUrl}?${params.toString()}`
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Get print jobs for a specific printer
   */
  async getPrinterQueueAsync(
    printerId: string,
    limit: number = 50
  ): Promise<QueuedPrintJobDto[]> {
    try {
      const response = await apiClient.get<QueuedPrintJobDto[]>(
        `${this.baseUrl}/printer/${printerId}`,
        { params: { limit } }
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Get queue statistics
   */
  async getQueueStatsAsync(): Promise<QueueStatsDto> {
    try {
      const response = await apiClient.get<QueueStatsDto>(
        `${this.baseUrl}/stats`
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Get queue statistics by model
   */
  async getModelStatsAsync(): Promise<QueuePrinterModelStatsDto[]> {
    try {
      const response = await apiClient.get<QueuePrinterModelStatsDto[]>(
        `${this.baseUrl}/stats/models`
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Get queue history with pagination
   */
  async getQueueHistoryAsync(
    limit: number = 50,
    offset: number = 0,
    sortBy: string = "completedAtUtc"
  ): Promise<QueueHistoryPageDto> {
    try {
      const response = await apiClient.get<QueueHistoryPageDto>(
        `${this.baseUrl}/history`,
        { params: { limit, offset, sortBy } }
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Enqueue a new print job
   */
  async enqueueJobAsync(
    request: EnqueueQueueJobRequest
  ): Promise<QueuedPrintJobDto> {
    try {
      const response = await apiClient.post<QueuedPrintJobDto>(
        `${this.baseUrl}`,
        request
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Update a print job
   */
  async updateJobAsync(
    jobId: string,
    request: UpdateQueueJobRequest
  ): Promise<QueuedPrintJobDto> {
    try {
      const response = await apiClient.put<QueuedPrintJobDto>(
        `${this.baseUrl}/jobs/${jobId}`,
        request
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Update job priority
   */
  async updateJobPriorityAsync(
    jobId: string,
    newPriority: number
  ): Promise<QueuedPrintJobDto> {
    try {
      const response = await apiClient.put<QueuedPrintJobDto>(
        `${this.baseUrl}/jobs/${jobId}/priority`,
        { newPriority }
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Pause a print job
   */
  async pauseJobAsync(jobId: string): Promise<QueuedPrintJobDto> {
    try {
      const response = await apiClient.post<QueuedPrintJobDto>(
        `${this.baseUrl}/jobs/${jobId}/pause`
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Resume a print job
   */
  async resumeJobAsync(jobId: string): Promise<QueuedPrintJobDto> {
    try {
      const response = await apiClient.post<QueuedPrintJobDto>(
        `${this.baseUrl}/jobs/${jobId}/resume`
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Cancel a print job
   */
  async cancelJobAsync(jobId: string): Promise<void> {
    try {
      await apiClient.delete(`${this.baseUrl}/jobs/${jobId}`);
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Rerun a completed job (add it back to queue)
   */
  async rerunJobAsync(jobId: string): Promise<QueuedPrintJobDto> {
    try {
      const response = await apiClient.post<QueuedPrintJobDto>(
        `${this.baseUrl}/jobs/${jobId}/rerun`
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Bulk cancel multiple print jobs
   */
  async bulkCancelJobsAsync(
    request: BulkCancelQueueJobsRequest
  ): Promise<QueueBulkOperationResultDto> {
    try {
      const response = await apiClient.post<QueueBulkOperationResultDto>(
        `${this.baseUrl}/bulk/cancel`,
        request
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Seed history from printer APIs (Phase 2)
   */
  async seedHistoryAsync(
    printerIds?: string[],
    daysBack: number = 30
  ): Promise<void> {
    try {
      await apiClient.post(`${this.baseUrl}/history/seed`, {
        printerIds,
        daysBack,
      });
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  // ============= JOB DETAILS OPERATIONS (Phase 3) =============

  /**
   * Get detailed information about a specific job
   */
  async getJobDetailsAsync(jobId: string): Promise<QueuedPrintJobDto> {
    try {
      const response = await apiClient.get<QueuedPrintJobDto>(
        `${this.baseUrl}/jobs/${jobId}`
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Update job details (name, priority, notes, tags, material, nozzle)
   */
  async updateJobDetailsAsync(
    jobId: string,
    updates: {
      name?: string;
      priority?: number;
      notes?: string;
      tags?: string[];
      requiredMaterialType?: string;
      requiredNozzleDiameter?: number;
    }
  ): Promise<QueuedPrintJobDto> {
    try {
      const response = await apiClient.put<QueuedPrintJobDto>(
        `${this.baseUrl}/jobs/${jobId}`,
        {
          name: updates.name,
          priority: updates.priority,
          notes: updates.notes,
          tags: updates.tags,
          requiredMaterialType: updates.requiredMaterialType,
          requiredNozzleDiameter: updates.requiredNozzleDiameter,
        }
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Update job notes only
   */
  async updateJobNotesAsync(jobId: string, notes: string): Promise<void> {
    try {
      await apiClient.put(`${this.baseUrl}/jobs/${jobId}/notes`, {
        notes: notes || null,
      });
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Get timeline events with optional filtering
   */
  async getTimelineAsync(
    dateFrom?: Date,
    dateTo?: Date,
    printerId?: string,
    filterStatus?: string,
    limit: number = 100
  ): Promise<TimelineEventDto[]> {
    try {
      const params = new URLSearchParams();
      if (dateFrom) params.append("dateFrom", dateFrom.toISOString());
      if (dateTo) params.append("dateTo", dateTo.toISOString());
      if (printerId) params.append("printerId", printerId);
      if (filterStatus) params.append("filterStatus", filterStatus);
      params.append("limit", limit.toString());

      const response = await apiClient.get<TimelineEventDto[]>(
        `${this.baseUrl}/timeline?${params.toString()}`
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Get state history for a specific job
   */
  async getJobStateHistoryAsync(jobId: string): Promise<JobStateHistoryDto> {
    try {
      const response = await apiClient.get<JobStateHistoryDto>(
        `${this.baseUrl}/jobs/${jobId}/state-history`
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  /**
   * Get duration analytics with optional filtering
   */
  async getDurationAnalyticsAsync(
    printerId?: string,
    dateFrom?: Date,
    dateTo?: Date
  ): Promise<DurationAnalyticsDto> {
    try {
      const params = new URLSearchParams();
      if (printerId) params.append("printerId", printerId);
      if (dateFrom) params.append("dateFrom", dateFrom.toISOString());
      if (dateTo) params.append("dateTo", dateTo.toISOString());

      const response = await apiClient.get<DurationAnalyticsDto>(
        `${this.baseUrl}/duration-analytics?${params.toString()}`
      );
      return response.data;
    } catch (error) {
      this.handleError(error);
      throw error;
    }
  }

  private handleError(error: unknown): void {
    if (axios.isAxiosError(error)) {
      const axiosError = error as AxiosError;
      const message =
        (axiosError.response?.data as Record<string, unknown>)?.error ||
        axiosError.message ||
        "An error occurred while communicating with the print queue service";
      console.error("Print Queue Service Error:", message, error);
    } else if (error instanceof Error) {
      console.error("Print Queue Service Error:", error.message);
    } else {
      console.error("Print Queue Service Error:", error);
    }
  }
}

export const printQueueService = new PrintQueueService();
