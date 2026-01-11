/**
 * React Query hook for completion predictions (Phase 4.2)
 */

import { useQuery } from '@tanstack/react-query';
import { predictionService } from '@/services/predictionService';
import type { CompletionPredictionDto, PrintJobStatisticsDto, DurationStatsDto } from '@/types/predictions';

/**
 * Hook for fetching completion predictions
 * Automatically refetches when prediction data changes
 */
export function useCompletionPrediction(
  jobId: string | null | undefined,
  enabled: boolean = true
) {
  return useQuery<CompletionPredictionDto>({
    queryKey: ['prediction', jobId],
    queryFn: () => {
      if (!jobId) throw new Error('Job ID is required');
      return predictionService.getPrediction(jobId);
    },
    staleTime: 60_000, // 1 minute
    gcTime: 5 * 60_000, // 5 minutes (renamed from cacheTime in RQ5)
    enabled: enabled && !!jobId,
    retry: 1,
  });
}

/**
 * Hook for fetching job statistics
 */
export function useJobStatistics(
  jobId: string | null | undefined,
  enabled: boolean = true
) {
  return useQuery<PrintJobStatisticsDto | null>({
    queryKey: ['statistics', jobId],
    queryFn: () => {
      if (!jobId) throw new Error('Job ID is required');
      return predictionService.getStatistics(jobId);
    },
    staleTime: 5 * 60_000, // 5 minutes
    gcTime: 30 * 60_000, // 30 minutes
    enabled: enabled && !!jobId,
    retry: 1,
  });
}

/**
 * Hook for fetching material statistics
 */
export function useMaterialStats(
  material?: string,
  printerId?: string,
  enabled: boolean = true
) {
  return useQuery<Record<string, DurationStatsDto>>({
    queryKey: ['materialStats', material, printerId],
    queryFn: () =>
      predictionService.getMaterialStats(material, printerId),
    staleTime: 10 * 60_000, // 10 minutes
    gcTime: 30 * 60_000, // 30 minutes
    enabled: enabled,
    retry: 1,
  });
}

/**
 * Hook for fetching model statistics
 */
export function useModelStats(
  modelId: string | null | undefined,
  material?: string,
  enabled: boolean = true
) {
  return useQuery<DurationStatsDto | null>({
    queryKey: ['modelStats', modelId, material],
    queryFn: () => {
      if (!modelId) throw new Error('Model ID is required');
      return predictionService.getModelStats(modelId, material);
    },
    staleTime: 10 * 60_000, // 10 minutes
    gcTime: 30 * 60_000, // 30 minutes
    enabled: enabled && !!modelId,
    retry: 1,
  });
}
