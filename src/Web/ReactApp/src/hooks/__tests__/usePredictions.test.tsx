import { renderHook, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useCompletionPrediction, useJobStatistics, useMaterialStats, useModelStats } from '../usePredictions';
import { predictionService } from '@/services/predictionService';

// Mock the prediction service
vi.mock('@/services/predictionService', () => ({
  predictionService: {
    getPrediction: vi.fn(),
    getStatistics: vi.fn(),
    getMaterialStats: vi.fn(),
    getModelStats: vi.fn(),
  }
}));

describe('usePredictions', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    vi.clearAllMocks();
  });

  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      {children}
    </QueryClientProvider>
  );

  describe('useCompletionPrediction', () => {
    it('should fetch completion prediction', async () => {
      const mockPrediction = {
        jobId: 'job-123',
        estimatedCompletion: '2024-01-01T12:00:00Z',
        confidence: 0.95,
      };

      vi.mocked(predictionService.getPrediction).mockResolvedValue(mockPrediction);

      const { result } = renderHook(() => useCompletionPrediction('job-123'), { wrapper });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(result.current.data).toEqual(mockPrediction);
      expect(predictionService.getPrediction).toHaveBeenCalledWith('job-123');
    });

    it('should not fetch when jobId is null', () => {
      const { result } = renderHook(() => useCompletionPrediction(null), { wrapper });

      expect(result.current.isPending).toBe(true);
      expect(result.current.fetchStatus).toBe('idle');
      expect(predictionService.getPrediction).not.toHaveBeenCalled();
    });

    it('should not fetch when enabled is false', () => {
      const { result } = renderHook(() => useCompletionPrediction('job-123', false), { wrapper });

      expect(result.current.isPending).toBe(true);
      expect(result.current.fetchStatus).toBe('idle');
      expect(predictionService.getPrediction).not.toHaveBeenCalled();
    });
  });

  describe('useJobStatistics', () => {
    it('should fetch job statistics', async () => {
      const mockStats = {
        jobId: 'job-123',
        duration: 3600,
        filamentUsed: 100,
      };

      vi.mocked(predictionService.getStatistics).mockResolvedValue(mockStats);

      const { result } = renderHook(() => useJobStatistics('job-123'), { wrapper });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(result.current.data).toEqual(mockStats);
      expect(predictionService.getStatistics).toHaveBeenCalledWith('job-123');
    });

    it('should not fetch when jobId is undefined', () => {
      const { result } = renderHook(() => useJobStatistics(undefined), { wrapper });

      expect(result.current.isPending).toBe(true);
      expect(result.current.fetchStatus).toBe('idle');
      expect(predictionService.getStatistics).not.toHaveBeenCalled();
    });
  });

  describe('useMaterialStats', () => {
    it('should fetch material statistics', async () => {
      const mockStats = {
        PLA: {
          avgDuration: 3600,
          avgFilamentUsed: 100,
          jobCount: 10,
        }
      };

      vi.mocked(predictionService.getMaterialStats).mockResolvedValue(mockStats);

      const { result } = renderHook(() => useMaterialStats('PLA', 'printer-1'), { wrapper });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(result.current.data).toEqual(mockStats);
      expect(predictionService.getMaterialStats).toHaveBeenCalledWith('PLA', 'printer-1');
    });

    it('should not fetch when enabled is false', () => {
      const { result } = renderHook(() => useMaterialStats('PLA', 'printer-1', false), { wrapper });

      expect(result.current.isPending).toBe(true);
      expect(result.current.fetchStatus).toBe('idle');
      expect(predictionService.getMaterialStats).not.toHaveBeenCalled();
    });
  });

  describe('useModelStats', () => {
    it('should fetch model statistics', async () => {
      const mockStats = {
        avgDuration: 3600,
        avgFilamentUsed: 100,
        jobCount: 5,
      };

      vi.mocked(predictionService.getModelStats).mockResolvedValue(mockStats);

      const { result } = renderHook(() => useModelStats('model-123', 'PLA'), { wrapper });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(result.current.data).toEqual(mockStats);
      expect(predictionService.getModelStats).toHaveBeenCalledWith('model-123', 'PLA');
    });

    it('should not fetch when modelId is null', () => {
      const { result } = renderHook(() => useModelStats(null), { wrapper });

      expect(result.current.isPending).toBe(true);
      expect(result.current.fetchStatus).toBe('idle');
      expect(predictionService.getModelStats).not.toHaveBeenCalled();
    });
  });
});
