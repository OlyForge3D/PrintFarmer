import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { apiClient } from './api';

// Artifact metadata DTO (from slicer-host GET /api/artifacts/{id}/metadata)
export interface ArtifactMetadataResponse {
  id: string;
  sliceJobId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  downloadUrl: string;
  createdAt: string;
}

// Artifact list item DTO (from slicer-host GET /api/artifacts/job/{jobId})
export interface ArtifactListItemResponse {
  id: string;
  jobId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  downloadUrl: string;
  createdAt: string;
}

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
  /**
   * Per-extruder filament colour overrides (hex, with '#'). Index = extruder index.
   * Cosmetic only — affects slice preview / G-code metadata, not print physics.
   */
  extruderFilamentColours?: string[];
  /** Multiple model file URLs for multi-model slice jobs (e.g., split/cut models). */
  modelFileUrls?: string[];
  /** Per-model transforms for multi-model slice jobs. Each entry corresponds positionally to a URL in modelFileUrls. */
  modelFileTransforms?: (string | null)[];
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
  sourceUrl?: string;
  sourceCreator?: string;
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

export interface SpoolCostResponse {
  costPerGram: number | null;
  currency: string;
  source: 'spool' | 'filament' | null;
}

export interface AddSliceToQueueRequest {
  priority?: string;
  spoolId?: number;
  copies?: number;
  requiredPrinterModel?: string;
  requiredMaterialType?: string;
  requiredNozzleDiameter?: number;
}

export interface AddSliceToQueueResponse {
  printJobId: string;
  queuePosition: number | null;
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
   * Fetch cost-per-gram for a given Spoolman spool (or filament).
   * Supply either spoolId or filamentId; the backend uses whichever is present.
   */
  async getSpoolCostPerGram(spoolId: number): Promise<SpoolCostResponse> {
    const response = await apiClient.request<SpoolCostResponse>({
      url: `/slice-cost/per-gram?spoolId=${spoolId}`,
      method: 'GET',
    });
    return response;
  }

  /**
   * Best-effort material cost = grams × per-gram cost.
   * Returns null when either input is missing or non-positive.
   */
  computeMaterialCostPerGram(
    grams: number | null | undefined,
    costPerGram: number | null | undefined,
  ): number | null {
    if (grams == null || costPerGram == null) return null;
    if (!(grams > 0) || !(costPerGram > 0)) return null;
    return grams * costPerGram;
  }

  /**
   * Add a completed slice job to the print queue.
   */
  async addSliceToQueue(jobId: string, payload: AddSliceToQueueRequest): Promise<AddSliceToQueueResponse> {
    const response = await apiClient.request<AddSliceToQueueResponse>({
      url: `/slice/${jobId}/add-to-queue`,
      method: 'POST',
      data: payload,
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
   * Parse an OrcaSlicer numeric setting that may be a number, a numeric
   * string, or an array of such values (Orca stores many settings as
   * per-extruder string arrays, e.g. `filament_cost: ["29.99"]`).
   * Returns null when no finite number can be derived.
   */
  parseOrcaNumeric(value: unknown): number | null {
    const raw = Array.isArray(value) ? value[0] : value;
    if (raw == null) return null;
    const n = typeof raw === 'number' ? raw : parseFloat(String(raw));
    return Number.isFinite(n) ? n : null;
  }

  /**
   * Best-effort material cost = grams/1000 × per-kg filament cost.
   * Returns null when either input is missing or non-positive so callers
   * can omit the value gracefully.
   */
  computeMaterialCost(
    grams: number | null | undefined,
    costPerKg: number | null | undefined,
  ): number | null {
    if (grams == null || costPerKg == null) return null;
    if (!(grams > 0) || !(costPerKg > 0)) return null;
    return (grams / 1000) * costPerKg;
  }

  /**
   * Format a best-effort material cost as a currency string, or null when no
   * cost could be computed.
   */
  formatMaterialCost(
    grams: number | null | undefined,
    costPerKg: number | null | undefined,
    currency = '$',
  ): string | null {
    const cost = this.computeMaterialCost(grams, costPerKg);
    if (cost == null) return null;
    return `${currency}${cost.toFixed(2)}`;
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

  /**
   * List all artifacts for a slice job (calls slicer-host GET /api/artifacts/job/{jobId}).
   */
  async getArtifactsByJob(jobId: string): Promise<ArtifactListItemResponse[]> {
    const response = await apiClient.request<ArtifactListItemResponse[]>({
      url: `/artifacts/job/${jobId}`,
      method: 'GET',
    });
    return response;
  }

  /**
   * Get artifact metadata (calls slicer-host GET /api/artifacts/{id}/metadata)
   */
  async getArtifactMetadata(artifactId: string): Promise<ArtifactMetadataResponse> {
    const response = await apiClient.request<ArtifactMetadataResponse>({
      url: `/artifacts/${artifactId}/metadata`,
      method: 'GET',
    });
    return response;
  }

  /**
   * Build the download URL for an artifact (no network call — just path construction).
   * Maps to GET /api/artifacts/{id} which streams the file as PhysicalFile.
   */
  getArtifactDownloadUrl(artifactId: string): string {
    return `${getApiBaseUrl()}/artifacts/${artifactId}`;
  }

  /**
   * Resolve the G-code download URL for a completed slice job.
   * Fetches artifact metadata and returns the download URL.
   */
  async getArtifactGcodeUrl(artifactId: string): Promise<string> {
    const metadata = await this.getArtifactMetadata(artifactId);
    return metadata.downloadUrl || this.getArtifactDownloadUrl(artifactId);
  }
}

export const sliceJobService = new SliceJobService();
