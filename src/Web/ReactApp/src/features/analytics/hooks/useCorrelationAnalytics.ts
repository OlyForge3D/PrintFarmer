import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';

export interface MaterialSuccessRate {
  material: string;
  totalJobs: number;
  completedJobs: number;
  successRate: number;
}

export interface PrinterMaterialPerformance {
  printerId: string;
  printerName: string;
  material: string;
  totalJobs: number;
  completedJobs: number;
  successRate: number;
}

export interface TemperatureQualityCorrelation {
  jobId: string;
  nozzleTemp: number;
  bedTemp: number;
  material: string;
  durationMinutes: number;
  success: boolean;
}

export interface DurationTrend {
  date: string;
  averageDurationMinutes: number;
  minDurationMinutes: number;
  maxDurationMinutes: number;
  jobCount: number;
}

export interface FailureReason {
  reason: string;
  count: number;
}

const CORRELATION_STALE_TIME = 300_000;

export function useMaterialSuccessRates(days?: number) {
  return useQuery<MaterialSuccessRate[]>({
    queryKey: ['correlation-analytics', 'material-success-rates', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/correlation-analytics/material-success-rates${params}`);
      return response.data;
    },
    staleTime: CORRELATION_STALE_TIME,
  });
}

export function usePrinterMaterialPerformance(days?: number) {
  return useQuery<PrinterMaterialPerformance[]>({
    queryKey: ['correlation-analytics', 'printer-material-performance', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/correlation-analytics/printer-material-performance${params}`);
      return response.data;
    },
    staleTime: CORRELATION_STALE_TIME,
  });
}

export function useTemperatureQualityCorrelation(days?: number) {
  return useQuery<TemperatureQualityCorrelation[]>({
    queryKey: ['correlation-analytics', 'temperature-quality', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/correlation-analytics/temperature-quality${params}`);
      return response.data;
    },
    staleTime: CORRELATION_STALE_TIME,
  });
}

export function useDurationTrends(days?: number) {
  return useQuery<DurationTrend[]>({
    queryKey: ['correlation-analytics', 'duration-trends', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/correlation-analytics/duration-trends${params}`);
      return response.data;
    },
    staleTime: CORRELATION_STALE_TIME,
  });
}

export function useFailureReasons(days?: number) {
  return useQuery<FailureReason[]>({
    queryKey: ['correlation-analytics', 'failure-reasons', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/correlation-analytics/failure-reasons${params}`);
      return response.data;
    },
    staleTime: CORRELATION_STALE_TIME,
  });
}
