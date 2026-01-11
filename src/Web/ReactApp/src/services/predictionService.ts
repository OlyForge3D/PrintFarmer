/**
 * API client for completion predictions (Phase 4.2)
 */

import axios from 'axios';
import type {
  CompletionPredictionDto,
  DurationStatsDto,
  PrintJobStatisticsDto,
  RecordCompletionRequest,
} from '@/types/predictions';

const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL || 'http://localhost:5245',
});

export const predictionService = {
  /**
   * Get predicted completion time for a job
   */
  async getPrediction(jobId: string): Promise<CompletionPredictionDto> {
    const { data } = await api.get<CompletionPredictionDto>(
      `/api/predictions/jobs/${jobId}/completion`
    );
    return data;
  },

  /**
   * Get recorded statistics for a completed job
   */
  async getStatistics(jobId: string): Promise<PrintJobStatisticsDto | null> {
    try {
      const { data } = await api.get<PrintJobStatisticsDto>(
        `/api/predictions/jobs/${jobId}/statistics`
      );
      return data;
    } catch (error) {
      if (axios.isAxiosError(error) && error.response?.status === 404) {
        return null; // No statistics recorded yet
      }
      throw error;
    }
  },

  /**
   * Get duration statistics by material type
   */
  async getMaterialStats(
    material?: string,
    printerId?: string,
    minSampleSize?: number
  ): Promise<Record<string, DurationStatsDto>> {
    const params = new URLSearchParams();
    if (material) params.append('material', material);
    if (printerId) params.append('printerId', printerId);
    if (minSampleSize) params.append('minSampleSize', minSampleSize.toString());

    const { data } = await api.get<Record<string, DurationStatsDto>>(
      `/api/predictions/stats/by-material?${params}`
    );
    return data;
  },

  /**
   * Get duration statistics for a printer model
   */
  async getModelStats(
    modelId: string,
    material?: string
  ): Promise<DurationStatsDto | null> {
    try {
      const params = new URLSearchParams();
      if (material) params.append('material', material);

      const { data } = await api.get<DurationStatsDto>(
        `/api/predictions/stats/model/${modelId}?${params}`
      );
      return data;
    } catch (error) {
      if (axios.isAxiosError(error) && error.response?.status === 404) {
        return null; // Insufficient data
      }
      throw error;
    }
  },

  /**
   * Record a job completion (admin only)
   */
  async recordCompletion(
    jobId: string,
    request: RecordCompletionRequest
  ): Promise<{ message: string }> {
    const { data } = await api.post<{ message: string }>(
      `/api/predictions/jobs/${jobId}/record-completion`,
      request
    );
    return data;
  },
};
