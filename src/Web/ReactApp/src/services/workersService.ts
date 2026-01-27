/**
 * Service for interacting with the Workers API
 * Provides methods to fetch worker information and availability
 */

import { WorkerResponse } from '@/types/worker';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';

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
  private getBase(): string {
    return `${getApiBaseUrl()}/workers`;
  }

  /**
   * Get all available workers (online with free slots)
   */
  async getAvailableWorkers(limit: number = 100): Promise<WorkerResponse[]> {
    const response = await fetch(`${this.getBase()}/available?limit=${limit}`, {
      headers: getAuthHeaders()
    });
    if (!response.ok) {
      throw new Error(await response.text() || 'Failed to fetch available workers');
    }
    return response.json();
  }

  /**
   * Get all workers
   */
  async getAllWorkers(limit: number = 100, offset: number = 0): Promise<WorkerResponse[]> {
    const response = await fetch(`${this.getBase()}?limit=${limit}&offset=${offset}`, {
      headers: getAuthHeaders()
    });
    if (!response.ok) {
      throw new Error(await response.text() || 'Failed to fetch workers');
    }
    return response.json();
  }

  /**
   * Get workers by status
   */
  async getWorkersByStatus(status: string, limit: number = 100): Promise<WorkerResponse[]> {
    const response = await fetch(`${this.getBase()}/by-status/${encodeURIComponent(status)}?limit=${limit}`, {
      headers: getAuthHeaders()
    });
    if (!response.ok) {
      throw new Error(await response.text() || 'Failed to fetch workers by status');
    }
    return response.json();
  }

  /**
   * Get worker by ID
   */
  async getWorkerById(id: string): Promise<WorkerResponse> {
    const response = await fetch(`${this.getBase()}/${id}`, {
      headers: getAuthHeaders()
    });
    if (!response.ok) {
      throw new Error(await response.text() || 'Failed to fetch worker');
    }
    return response.json();
  }

  /**
   * Get active jobs assigned to a specific worker
   */
  async getWorkerJobs(workerId: string): Promise<WorkerJobResponse[]> {
    const response = await fetch(`${this.getBase()}/${workerId}/jobs`, {
      headers: getAuthHeaders()
    });
    if (!response.ok) {
      throw new Error(await response.text() || 'Failed to fetch worker jobs');
    }
    return response.json();
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
