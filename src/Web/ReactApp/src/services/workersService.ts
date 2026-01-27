/**
 * Service for interacting with the Workers API
 * Provides methods to fetch worker information and availability
 */

import { WorkerResponse } from '@/types/worker';
import { apiClient } from './api';

export interface WorkerJobResponse {
  jobId: string;
  modelFileName: string;
  status: string;
  progressPercent: number;
  progressMessage?: string;
  startedAt?: string;
  priority: number;
}

class WorkersService {
  /**
   * Get all available workers (online with free slots)
   */
  async getAvailableWorkers(limit: number = 100): Promise<WorkerResponse[]> {
    const response = await apiClient.get<WorkerResponse[]>(`/workers/available?limit=${limit}`);
    return response.data;
  }

  /**
   * Get all workers
   */
  async getAllWorkers(limit: number = 100, offset: number = 0): Promise<WorkerResponse[]> {
    const response = await apiClient.get<WorkerResponse[]>(`/workers?limit=${limit}&offset=${offset}`);
    return response.data;
  }

  /**
   * Get workers by status
   */
  async getWorkersByStatus(status: string, limit: number = 100): Promise<WorkerResponse[]> {
    const response = await apiClient.get<WorkerResponse[]>(`/workers/by-status/${encodeURIComponent(status)}?limit=${limit}`);
    return response.data;
  }

  /**
   * Get worker by ID
   */
  async getWorkerById(id: string): Promise<WorkerResponse> {
    const response = await apiClient.get<WorkerResponse>(`/workers/${id}`);
    return response.data;
  }

  /**
   * Get active jobs assigned to a specific worker
   */
  async getWorkerJobs(workerId: string): Promise<WorkerJobResponse[]> {
    const response = await apiClient.get<WorkerJobResponse[]>(`/workers/${workerId}/jobs`);
    return response.data;
  }

  /**
   * Filter workers by required capabilities
   * Client-side filtering since the API doesn't expose this endpoint publicly
   */
  filterWorkersByCapabilities(
    workers: WorkerResponse[],
    requiredCapabilities: string[]
  ): WorkerResponse[] {
    if (requiredCapabilities.length === 0) {
      return workers;
    }

    return workers.filter(worker => {
      // Worker must have ALL required capabilities
      return requiredCapabilities.every(requiredCap =>
        worker.capabilities.some(cap => cap.toLowerCase() === requiredCap.toLowerCase())
      );
    });
  }
}

export const workersService = new WorkersService();
