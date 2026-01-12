import { apiClient } from './api';

// Worker DTOs matching backend
export interface WorkerResponse {
  id: string;
  serviceId: string;
  name: string;
  endpointUrl: string;
  capabilities: string[];
  status: string;
  freeSlots: number;
  totalSlots: number;
  activeJobs: number;
  completedJobs: number;
  failedJobs: number;
  averageProcessingTimeSeconds: number;
  lastHeartbeat: string;
  registeredAt: string;
  onlineAt?: string;
  offlineAt?: string;
  apiKey: string;
  version: string;
  metadata?: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  isDisabled: boolean;
  disabledReason?: string;
}

export interface DisableWorkerRequest {
  reason: string;
}

export interface WorkerJobResponse {
  jobId: string;
  modelFileName: string;
  status: string;
  progressPercent: number;
  progressMessage?: string;
  startedAt?: string;
  priority: number;
}

// Worker statuses
export enum WorkerStatus {
  Offline = 'Offline',
  Online = 'Online',
  Busy = 'Busy',
  Error = 'Error',
  Draining = 'Draining'
}

export class WorkerService {
  /**
   * Get all workers with optional pagination
   */
  async getAllWorkers(limit?: number, offset?: number): Promise<WorkerResponse[]> {
    const params = new URLSearchParams();
    if (limit !== undefined) params.append('limit', limit.toString());
    if (offset !== undefined) params.append('offset', offset.toString());
    
    const url = `/workers${params.toString() ? `?${params.toString()}` : ''}`;
    const response = await apiClient.request<WorkerResponse[]>({ url, method: 'GET' });
    return response;
  }

  /**
   * Get a specific worker by ID
   */
  async getWorkerById(id: string): Promise<WorkerResponse> {
    const response = await apiClient.request<WorkerResponse>({ url: `/workers/${id}`, method: 'GET' });
    return response;
  }

  /**
   * Get workers by status
   */
  async getWorkersByStatus(status: WorkerStatus): Promise<WorkerResponse[]> {
    const response = await apiClient.request<WorkerResponse[]>({ url: `/workers/by-status/${status}`, method: 'GET' });
    return response;
  }

  /**
   * Get all available workers (Online with free slots)
   */
  async getAvailableWorkers(): Promise<WorkerResponse[]> {
    const response = await apiClient.request<WorkerResponse[]>({ url: '/workers/available', method: 'GET' });
    return response;
  }

  /**
   * Disable a worker (admin only)
   */
  async disableWorker(id: string, reason: string): Promise<void> {
    await apiClient.request({ url: `/workers/${id}/disable`, method: 'POST', data: { reason } });
  }

  /**
   * Enable a worker (admin only)
   */
  async enableWorker(id: string): Promise<void> {
    await apiClient.request({ url: `/workers/${id}/enable`, method: 'POST' });
  }

  /**
   * Delete a worker (admin only)
   */
  async deleteWorker(id: string): Promise<void> {
    await apiClient.request({ url: `/workers/${id}`, method: 'DELETE' });
  }

  /**
   * Update worker total slots (admin only)
   */
  async updateWorkerSlots(id: string, totalSlots: number): Promise<WorkerResponse> {
    const response = await apiClient.request<WorkerResponse>({ 
      url: `/workers/${id}/slots`, 
      method: 'PUT', 
      data: { totalSlots } 
    });
    return response;
  }

  /**
   * Get active jobs for a worker
   */
  async getWorkerJobs(workerId: string): Promise<WorkerJobResponse[]> {
    const response = await apiClient.request<WorkerJobResponse[]>({ 
      url: `/workers/${workerId}/jobs`, 
      method: 'GET' 
    });
    return response;
  }

  /**
   * Calculate worker utilization percentage
   */
  calculateUtilization(worker: WorkerResponse): number {
    if (worker.totalSlots === 0) return 0;
    return ((worker.totalSlots - worker.freeSlots) / worker.totalSlots) * 100;
  }

  /**
   * Calculate worker success rate percentage
   */
  calculateSuccessRate(worker: WorkerResponse): number {
    const total = worker.completedJobs + worker.failedJobs;
    if (total === 0) return 100;
    return (worker.completedJobs / total) * 100;
  }

  /**
   * Get formatted uptime string
   */
  getUptime(worker: WorkerResponse): string {
    if (!worker.onlineAt) return 'N/A';
    
    const onlineTime = new Date(worker.onlineAt);
    const now = new Date();
    const diffMs = now.getTime() - onlineTime.getTime();
    
    const hours = Math.floor(diffMs / (1000 * 60 * 60));
    const minutes = Math.floor((diffMs % (1000 * 60 * 60)) / (1000 * 60));
    
    if (hours > 24) {
      const days = Math.floor(hours / 24);
      return `${days}d ${hours % 24}h`;
    }
    return `${hours}h ${minutes}m`;
  }

  /**
   * Check if worker heartbeat is stale (>2 minutes)
   */
  isHeartbeatStale(worker: WorkerResponse): boolean {
    const lastHeartbeat = new Date(worker.lastHeartbeat);
    const now = new Date();
    const diffMs = now.getTime() - lastHeartbeat.getTime();
    return diffMs > 2 * 60 * 1000; // 2 minutes
  }
}

export const workerService = new WorkerService();
