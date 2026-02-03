import { describe, it, expect, vi, beforeEach } from 'vitest';
import { workersService } from '../workersService';
import { apiClient } from '../api';
import { WorkerResponse } from '@/types/worker';

vi.mock('../api', () => ({
  apiClient: {
    get: vi.fn(),
  },
}));

describe('workersService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('getAvailableWorkers', () => {
    it('should get available workers with default limit', async () => {
      const mockWorkers: WorkerResponse[] = [
        {
          id: 'worker-1',
          name: 'Worker 1',
          status: 'Available',
          capabilities: ['orcaslicer'],
          freeSlots: 2,
        } as WorkerResponse,
      ];

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockWorkers });

      const result = await workersService.getAvailableWorkers();

      expect(apiClient.get).toHaveBeenCalledWith('/workers/available?limit=100');
      expect(result).toEqual(mockWorkers);
    });

    it('should get available workers with custom limit', async () => {
      const mockWorkers: WorkerResponse[] = [];

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockWorkers });

      const result = await workersService.getAvailableWorkers(50);

      expect(apiClient.get).toHaveBeenCalledWith('/workers/available?limit=50');
      expect(result).toEqual(mockWorkers);
    });
  });

  describe('getAllWorkers', () => {
    it('should get all workers with default pagination', async () => {
      const mockWorkers: WorkerResponse[] = [
        {
          id: 'worker-1',
          name: 'Worker 1',
          status: 'Busy',
          capabilities: ['orcaslicer', 'prusaslicer'],
          freeSlots: 0,
        } as WorkerResponse,
        {
          id: 'worker-2',
          name: 'Worker 2',
          status: 'Available',
          capabilities: ['orcaslicer'],
          freeSlots: 1,
        } as WorkerResponse,
      ];

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockWorkers });

      const result = await workersService.getAllWorkers();

      expect(apiClient.get).toHaveBeenCalledWith('/workers?limit=100&offset=0');
      expect(result).toEqual(mockWorkers);
      expect(result).toHaveLength(2);
    });

    it('should get all workers with custom pagination', async () => {
      const mockWorkers: WorkerResponse[] = [];

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockWorkers });

      const result = await workersService.getAllWorkers(50, 100);

      expect(apiClient.get).toHaveBeenCalledWith('/workers?limit=50&offset=100');
      expect(result).toEqual(mockWorkers);
    });
  });

  describe('getWorkersByStatus', () => {
    it('should get workers by status', async () => {
      const mockWorkers: WorkerResponse[] = [
        {
          id: 'worker-1',
          name: 'Worker 1',
          status: 'Available',
          capabilities: [],
          freeSlots: 3,
        } as WorkerResponse,
      ];

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockWorkers });

      const result = await workersService.getWorkersByStatus('Available');

      expect(apiClient.get).toHaveBeenCalledWith('/workers/by-status/Available?limit=100');
      expect(result).toEqual(mockWorkers);
    });

    it('should encode status with special characters', async () => {
      const mockWorkers: WorkerResponse[] = [];

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockWorkers });

      await workersService.getWorkersByStatus('Status With Spaces');

      expect(apiClient.get).toHaveBeenCalledWith('/workers/by-status/Status%20With%20Spaces?limit=100');
    });
  });

  describe('getWorkerById', () => {
    it('should get worker by ID', async () => {
      const mockWorker: WorkerResponse = {
        id: 'worker-123',
        name: 'Worker 123',
        status: 'Busy',
        capabilities: ['orcaslicer'],
        freeSlots: 0,
      } as WorkerResponse;

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockWorker });

      const result = await workersService.getWorkerById('worker-123');

      expect(apiClient.get).toHaveBeenCalledWith('/workers/worker-123');
      expect(result).toEqual(mockWorker);
    });
  });

  describe('getWorkerJobs', () => {
    it('should get active jobs for a worker', async () => {
      const mockJobs = [
        {
          jobId: 'job-1',
          modelFileName: 'model1.stl',
          status: 'Processing',
          progressPercent: 50,
          progressMessage: 'Slicing...',
          priority: 1,
        },
        {
          jobId: 'job-2',
          modelFileName: 'model2.stl',
          status: 'Queued',
          progressPercent: 0,
          priority: 2,
        },
      ];

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockJobs });

      const result = await workersService.getWorkerJobs('worker-123');

      expect(apiClient.get).toHaveBeenCalledWith('/workers/worker-123/jobs');
      expect(result).toEqual(mockJobs);
      expect(result).toHaveLength(2);
    });

    it('should return empty array when worker has no jobs', async () => {
      vi.mocked(apiClient.get).mockResolvedValue({ data: [] });

      const result = await workersService.getWorkerJobs('worker-empty');

      expect(result).toEqual([]);
    });
  });

  describe('filterWorkersByCapabilities', () => {
    const mockWorkers: WorkerResponse[] = [
      {
        id: 'worker-1',
        name: 'Worker 1',
        status: 'Available',
        capabilities: ['orcaslicer', 'prusaslicer'],
        freeSlots: 1,
      } as WorkerResponse,
      {
        id: 'worker-2',
        name: 'Worker 2',
        status: 'Available',
        capabilities: ['orcaslicer'],
        freeSlots: 2,
      } as WorkerResponse,
      {
        id: 'worker-3',
        name: 'Worker 3',
        status: 'Available',
        capabilities: ['prusaslicer'],
        freeSlots: 1,
      } as WorkerResponse,
    ];

    it('should return all workers when no capabilities required', () => {
      const result = workersService.filterWorkersByCapabilities(mockWorkers, []);

      expect(result).toEqual(mockWorkers);
      expect(result).toHaveLength(3);
    });

    it('should filter workers by single capability', () => {
      const result = workersService.filterWorkersByCapabilities(mockWorkers, ['orcaslicer']);

      expect(result).toHaveLength(2);
      expect(result[0].id).toBe('worker-1');
      expect(result[1].id).toBe('worker-2');
    });

    it('should filter workers by multiple capabilities (must have ALL)', () => {
      const result = workersService.filterWorkersByCapabilities(mockWorkers, ['orcaslicer', 'prusaslicer']);

      expect(result).toHaveLength(1);
      expect(result[0].id).toBe('worker-1');
    });

    it('should be case insensitive', () => {
      const result = workersService.filterWorkersByCapabilities(mockWorkers, ['ORCASLICER']);

      expect(result).toHaveLength(2);
    });

    it('should return empty array when no workers match', () => {
      const result = workersService.filterWorkersByCapabilities(mockWorkers, ['nonexistent']);

      expect(result).toEqual([]);
    });

    it('should return empty array when filtering empty worker list', () => {
      const result = workersService.filterWorkersByCapabilities([], ['orcaslicer']);

      expect(result).toEqual([]);
    });
  });
});
