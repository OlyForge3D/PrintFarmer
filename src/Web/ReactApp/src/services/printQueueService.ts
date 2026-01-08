import { getApiBaseUrl } from "@/common/utils/apiUrlHelpers";
import axios, { AxiosInstance, AxiosError } from "axios";

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
  private apiClient: AxiosInstance;
  private baseUrl = "printQueue";

  constructor() {
    const apiBaseUrl = getApiBaseUrl();
    this.apiClient = axios.create({
      baseURL: apiBaseUrl,
      headers: {
        "Content-Type": "application/json",
      },
      timeout: 30000,
    });
  }

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

      const response = await this.apiClient.get<QueuedPrintJobWithFileMetaDto[]>(
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
      const response = await this.apiClient.get<QueuedPrintJobDto[]>(
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
      const response = await this.apiClient.get<QueueStatsDto>(
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
      const response = await this.apiClient.get<QueuePrinterModelStatsDto[]>(
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
      const response = await this.apiClient.get<QueueHistoryPageDto>(
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
      const response = await this.apiClient.post<QueuedPrintJobDto>(
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
      const response = await this.apiClient.put<QueuedPrintJobDto>(
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
      const response = await this.apiClient.put<QueuedPrintJobDto>(
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
      const response = await this.apiClient.post<QueuedPrintJobDto>(
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
      const response = await this.apiClient.post<QueuedPrintJobDto>(
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
      await this.apiClient.delete(`${this.baseUrl}/jobs/${jobId}`);
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
      const response = await this.apiClient.post<QueueBulkOperationResultDto>(
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
      await this.apiClient.post(`${this.baseUrl}/history/seed`, {
        printerIds,
        daysBack,
      });
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
