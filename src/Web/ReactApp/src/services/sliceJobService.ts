import { apiClient } from './api';

// Slice Job DTOs matching backend
export interface SubmitSliceJobRequest {
  userId: string;
  printerId?: string;
  modelFileUrl: string;
  modelFileName: string;
  slicerEngine: number;
  slicerProfileJson: string;
  requiredCapabilitiesJson: string;
  priority?: number;
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

export class SliceJobService {
  /**
   * Submit a new slicing job
   */
  async submitJob(request: SubmitSliceJobRequest): Promise<SubmitSliceJobResponse> {
    const response = await apiClient.request<SubmitSliceJobResponse>({
      url: '/slice',
      method: 'POST',
      data: request
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
      case SliceJobStatus.Queued: return 'text-blue-600 bg-blue-100';
      case SliceJobStatus.Processing: return 'text-yellow-600 bg-yellow-100';
      case SliceJobStatus.Completed: return 'text-green-600 bg-green-100';
      case SliceJobStatus.Failed: return 'text-red-600 bg-red-100';
      case SliceJobStatus.Cancelled: return 'text-gray-600 bg-gray-100';
      default: return 'text-gray-600 bg-gray-100';
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
}

export const sliceJobService = new SliceJobService();
