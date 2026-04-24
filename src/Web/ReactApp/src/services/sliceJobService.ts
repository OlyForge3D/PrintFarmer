import { apiClient } from './api';

// Slice Job DTOs matching backend
export interface SubmitSliceJobRequest {
  userId: string;
  printerId?: string;
  modelFileUrl: string;
  modelFileName: string;
  slicerEngine: number;
  slicerProfileJson: string;
  // Optional reference to a stored slicer profile (takes precedence over slicerProfileJson if provided)
  slicerProfileId?: string;
  requiredCapabilitiesJson: string;
  priority?: number;
  modelTransformJson?: string;
  /** Per-extruder filament profile names for multi-toolhead printers (index = extruder index). */
  extruderFilamentProfileNames?: string[];
  /** Multiple model file URLs for multi-model slice jobs (e.g., split/cut models). */
  modelFileUrls?: string[];
}

export interface SubmitSliceJobResponse {
  jobId: string;
  status: string;
  queuedAt: string;
  queuePosition: number;
}

export interface SliceJobStatusResponse {
  id: string;
  status: string;
  progressPercent: number;
  progressMessage?: string;
  queuedAt: string;
  startedAt?: string;
  completedAt?: string;
  resultFileUrl?: string;
  errorMessage?: string;
  estimatedPrintTimeSeconds?: number;
  filamentUsedGrams?: number;
  workerId?: string;
  artifactsCount?: number;
  artifactsTotalBytes?: number;
}

// Job statuses
export enum SliceJobStatus {
  Queued = 'Queued',
  Processing = 'Processing',
  Completed = 'Completed',
  Failed = 'Failed',
  Cancelled = 'Cancelled'
}

// Slicer engines
export enum SlicerEngine {
  OrcaSlicer = 0,
  PrusaSlicer = 1
}

export interface SendToPrinterRequest {
  printerId: string;
  startPrint: boolean;
}

export interface SendToPrinterResponse {
  jobId: string;
  printerId: string;
  fileName: string;
  printStarted: boolean;
  message: string;
}

export class SliceJobService {
  /**
   * Submit a new slicing job
   */
  async submitJob(request: SubmitSliceJobRequest): Promise<SubmitSliceJobResponse> {
    const response = await apiClient.request<SubmitSliceJobResponse>({
      url: '/slice/',
      method: 'POST',
      data: request
    });
    return response;
  }

  /**
   * Send completed gcode to a printer for printing
   */
  async sendToPrinter(jobId: string, printerId: string, startPrint: boolean): Promise<SendToPrinterResponse> {
    const response = await apiClient.request<SendToPrinterResponse>({
      url: `/slice/${jobId}/send-to-printer`,
      method: 'POST',
      data: { printerId, startPrint } satisfies SendToPrinterRequest
    });
    return response;
  }

  /**
   * Get job status by ID
   */
  async getJobStatus(jobId: string): Promise<SliceJobStatusResponse> {
    const response = await apiClient.request<SliceJobStatusResponse>({
      url: `/slice/${jobId}`,
      method: 'GET'
    });
    return response;
  }

  /**
   * Cancel a job
   */
  async cancelJob(jobId: string): Promise<void> {
    await apiClient.request({
      url: `/slice/${jobId}/cancel`,
      method: 'POST'
    });
  }

  /**
   * Retry a failed job
   */
  async retryJob(jobId: string): Promise<SliceJobStatusResponse> {
    const response = await apiClient.request<SliceJobStatusResponse>({
      url: `/slice/${jobId}/retry`,
      method: 'POST'
    });
    return response;
  }

  /**
   * Get current user's jobs with pagination
   */
  async getMyJobs(limit?: number, offset?: number): Promise<SliceJobStatusResponse[]> {
    const params = new URLSearchParams();
    if (limit !== undefined) params.append('limit', limit.toString());
    if (offset !== undefined) params.append('offset', offset.toString());
    
    const url = `/slice/my-jobs${params.toString() ? `?${params.toString()}` : ''}`;
    const response = await apiClient.request<SliceJobStatusResponse[]>({
      url,
      method: 'GET'
    });
    return response;
  }

  /**
   * Get job queue (all queued jobs - admin endpoint)
   */
  async getQueue(): Promise<SliceJobStatusResponse[]> {
    const response = await apiClient.request<SliceJobStatusResponse[]>({
      url: '/slice/queue',
      method: 'GET'
    });
    return response;
  }

  /**
   * Get human-readable status text
   */
  getStatusText(status: SliceJobStatus): string {
    switch (status) {
      case SliceJobStatus.Queued: return 'Queued';
      case SliceJobStatus.Processing: return 'Processing';
      case SliceJobStatus.Completed: return 'Completed';
      case SliceJobStatus.Failed: return 'Failed';
      case SliceJobStatus.Cancelled: return 'Cancelled';
      default: return status;
    }
  }

  /**
   * Get status color for UI
   */
  getStatusColor(status: SliceJobStatus): string {
    switch (status) {
      case SliceJobStatus.Queued: return 'text-pf-accent bg-pf-accent-bg/15';
      case SliceJobStatus.Processing: return 'text-pf-warning bg-pf-warning/10';
      case SliceJobStatus.Completed: return 'text-pf-success bg-pf-success/10';
      case SliceJobStatus.Failed: return 'text-pf-error bg-pf-error/10';
      case SliceJobStatus.Cancelled: return 'text-pf-text-secondary bg-pf-bg-1';
      default: return 'text-pf-text-secondary bg-pf-bg-1';
    }
  }

  /**
   * Calculate estimated time remaining
   */
  getEstimatedTimeRemaining(job: SliceJobStatusResponse): string | null {
    if (!job.startedAt || job.progressPercent <= 0) return null;
    
    const startTime = new Date(job.startedAt);
    const now = new Date();
    const elapsedMs = now.getTime() - startTime.getTime();
    const elapsedSeconds = elapsedMs / 1000;
    
    const estimatedTotalSeconds = (elapsedSeconds / job.progressPercent) * 100;
    const remainingSeconds = estimatedTotalSeconds - elapsedSeconds;
    
    if (remainingSeconds < 60) {
      return `${Math.round(remainingSeconds)}s`;
    } else if (remainingSeconds < 3600) {
      return `${Math.round(remainingSeconds / 60)}m`;
    } else {
      const hours = Math.floor(remainingSeconds / 3600);
      const minutes = Math.round((remainingSeconds % 3600) / 60);
      return `${hours}h ${minutes}m`;
    }
  }

  /**
   * Format file size in human-readable format
   */
  formatFilamentUsed(grams: number): string {
    if (grams < 1000) {
      return `${grams.toFixed(1)}g`;
    }
    return `${(grams / 1000).toFixed(2)}kg`;
  }

  /**
   * Format print time in human-readable format
   */
  formatPrintTime(seconds: number): string {
    if (seconds < 60) {
      return `${Math.round(seconds)}s`;
    } else if (seconds < 3600) {
      return `${Math.round(seconds / 60)}m`;
    } else {
      const hours = Math.floor(seconds / 3600);
      const minutes = Math.round((seconds % 3600) / 60);
      return `${hours}h ${minutes}m`;
    }
  }

  /**
   * Format file size in human-readable format
   */
  formatFileSize(bytes: number): string {
    if (bytes < 1024) {
      return `${bytes} B`;
    } else if (bytes < 1024 * 1024) {
      return `${(bytes / 1024).toFixed(1)} KB`;
    } else {
      return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
    }
  }
}

export const sliceJobService = new SliceJobService();
