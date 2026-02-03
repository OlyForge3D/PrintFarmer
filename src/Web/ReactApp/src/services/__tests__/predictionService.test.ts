import { describe, it, expect, vi, beforeEach } from 'vitest';
import { predictionService } from '../predictionService';
import { apiClient } from '../api';

// Mock the api client
vi.mock('../api', () => ({
  apiClient: {
    getPrediction: vi.fn(),
    getStatistics: vi.fn(),
    getMaterialStats: vi.fn(),
    getModelStats: vi.fn(),
    recordCompletion: vi.fn(),
  }
}));

describe('predictionService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('getPrediction', () => {
    it('should get prediction for a job', async () => {
      const mockPrediction = {
        jobId: 'job-123',
        estimatedCompletion: '2024-01-01T12:00:00Z',
        confidence: 0.95,
        remainingSeconds: 3600,
      };

      vi.mocked(apiClient.getPrediction).mockResolvedValue(mockPrediction);

      const result = await predictionService.getPrediction('job-123');

      expect(result).toEqual(mockPrediction);
      expect(apiClient.getPrediction).toHaveBeenCalledWith('job-123');
    });

    it('should handle errors', async () => {
      const error = new Error('Failed to fetch prediction');
      vi.mocked(apiClient.getPrediction).mockRejectedValue(error);

      await expect(predictionService.getPrediction('job-123')).rejects.toThrow('Failed to fetch prediction');
    });
  });

  describe('getStatistics', () => {
    it('should get statistics for a job', async () => {
      const mockStats = {
        jobId: 'job-123',
        duration: 3600,
        filamentUsed: 100,
        completedAt: '2024-01-01T12:00:00Z',
      };

      vi.mocked(apiClient.getStatistics).mockResolvedValue(mockStats);

      const result = await predictionService.getStatistics('job-123');

      expect(result).toEqual(mockStats);
      expect(apiClient.getStatistics).toHaveBeenCalledWith('job-123');
    });

    it('should return null for non-existent job', async () => {
      vi.mocked(apiClient.getStatistics).mockResolvedValue(null);

      const result = await predictionService.getStatistics('non-existent');

      expect(result).toBeNull();
    });
  });

  describe('getMaterialStats', () => {
    it('should get material statistics', async () => {
      const mockStats = {
        PLA: {
          avgDuration: 3600,
          avgFilamentUsed: 100,
          jobCount: 10,
          minDuration: 1800,
          maxDuration: 7200,
        }
      };

      vi.mocked(apiClient.getMaterialStats).mockResolvedValue(mockStats);

      const result = await predictionService.getMaterialStats('PLA', 'printer-1');

      expect(result).toEqual(mockStats);
      expect(apiClient.getMaterialStats).toHaveBeenCalledWith('PLA', 'printer-1', undefined);
    });

    it('should get material stats with min sample size', async () => {
      const mockStats = {
        PETG: {
          avgDuration: 4200,
          avgFilamentUsed: 120,
          jobCount: 15,
          minDuration: 2000,
          maxDuration: 8000,
        }
      };

      vi.mocked(apiClient.getMaterialStats).mockResolvedValue(mockStats);

      const result = await predictionService.getMaterialStats('PETG', 'printer-1', 5);

      expect(result).toEqual(mockStats);
      expect(apiClient.getMaterialStats).toHaveBeenCalledWith('PETG', 'printer-1', 5);
    });

    it('should handle optional parameters', async () => {
      vi.mocked(apiClient.getMaterialStats).mockResolvedValue({});

      await predictionService.getMaterialStats();

      expect(apiClient.getMaterialStats).toHaveBeenCalledWith(undefined, undefined, undefined);
    });
  });

  describe('getModelStats', () => {
    it('should get model statistics', async () => {
      const mockStats = {
        avgDuration: 3600,
        avgFilamentUsed: 100,
        jobCount: 5,
        minDuration: 1800,
        maxDuration: 7200,
      };

      vi.mocked(apiClient.getModelStats).mockResolvedValue(mockStats);

      const result = await predictionService.getModelStats('model-123', 'PLA');

      expect(result).toEqual(mockStats);
      expect(apiClient.getModelStats).toHaveBeenCalledWith('model-123', 'PLA');
    });

    it('should work without material parameter', async () => {
      const mockStats = {
        avgDuration: 3600,
        avgFilamentUsed: 100,
        jobCount: 5,
        minDuration: 1800,
        maxDuration: 7200,
      };

      vi.mocked(apiClient.getModelStats).mockResolvedValue(mockStats);

      const result = await predictionService.getModelStats('model-123');

      expect(result).toEqual(mockStats);
      expect(apiClient.getModelStats).toHaveBeenCalledWith('model-123', undefined);
    });

    it('should return null for non-existent model', async () => {
      vi.mocked(apiClient.getModelStats).mockResolvedValue(null);

      const result = await predictionService.getModelStats('non-existent');

      expect(result).toBeNull();
    });
  });

  describe('recordCompletion', () => {
    it('should record job completion', async () => {
      const request = {
        actualDuration: 3600,
        actualFilamentUsed: 100,
        success: true,
      };

      vi.mocked(apiClient.recordCompletion).mockResolvedValue(undefined);

      const result = await predictionService.recordCompletion('job-123', request);

      expect(result).toEqual({ message: 'Completion recorded' });
      expect(apiClient.recordCompletion).toHaveBeenCalledWith('job-123', request);
    });

    it('should handle errors when recording completion', async () => {
      const request = {
        actualDuration: 3600,
        actualFilamentUsed: 100,
        success: false,
      };

      const error = new Error('Failed to record completion');
      vi.mocked(apiClient.recordCompletion).mockRejectedValue(error);

      await expect(predictionService.recordCompletion('job-123', request)).rejects.toThrow('Failed to record completion');
    });
  });
});
