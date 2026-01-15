/**
 * API client for completion predictions (Phase 4.2)
 * Delegated to apiClient singleton
 */

import { apiClient } from '@/services/api';
import type {
  CompletionPredictionDto,
  DurationStatsDto,
  PrintJobStatisticsDto,
  RecordCompletionRequest,
} from '@/types/predictions';

/**
 * Prediction service - delegated to apiClient singleton
 * apiClient handles authentication, correlation IDs, and error handling automatically
 */
export const predictionService = {
  /**
   * Get predicted completion time for a job
   */
  async getPrediction(jobId: string): Promise<CompletionPredictionDto> {
    return apiClient.getPrediction(jobId);
  },

  /**
   * Get recorded statistics for a completed job
   */
  async getStatistics(jobId: string): Promise<PrintJobStatisticsDto | null> {
    return apiClient.getStatistics(jobId);
  },

  /**
   * Get duration statistics by material type
   */
  async getMaterialStats(
    material?: string,
    printerId?: string,
    minSampleSize?: number
  ): Promise<Record<string, DurationStatsDto>> {
    return apiClient.getMaterialStats(material, printerId, minSampleSize);
  },

  /**
   * Get duration statistics for a printer model
   */
  async getModelStats(
    modelId: string,
    material?: string
  ): Promise<DurationStatsDto | null> {
    return apiClient.getModelStats(modelId, material);
  },

  /**
   * Record a job completion (admin only)
   */
  async recordCompletion(
    jobId: string,
    request: RecordCompletionRequest
  ): Promise<{ message: string }> {
    await apiClient.recordCompletion(jobId, request);
    return { message: 'Completion recorded' };
  },
};
