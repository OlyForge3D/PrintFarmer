import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';

export interface StatisticsSummary {
  totalJobs: number;
  completedJobs: number;
  failedJobs: number;
  cancelledJobs: number;
  successRate: number;
  totalCost: number;
  totalFilamentGrams: number;
  totalPrintHours: number;
}

export interface DailyJobCount {
  date: string;
  completed: number;
  failed: number;
  cancelled: number;
}

export interface DailyCost {
  date: string;
  cost: number;
}

export interface FilamentByMaterial {
  material: string;
  grams: number;
}

export interface PrinterUtilization {
  printerId: string;
  printerName: string;
  totalJobs: number;
  completedJobs: number;
  failedJobs: number;
  totalPrintHours: number;
  successRate: number;
}

function buildStatsParams(days?: number, startDate?: string, endDate?: string): string {
  const params = new URLSearchParams();
  if (startDate && endDate) {
    params.set('startDate', startDate);
    params.set('endDate', endDate);
  } else if (days) {
    params.set('days', String(days));
  }
  const query = params.toString();
  return query ? `?${query}` : '';
}

export function useStatisticsSummary(days?: number, startDate?: string, endDate?: string) {
  return useQuery<StatisticsSummary>({
    queryKey: ['statistics', 'summary', days, startDate, endDate],
    queryFn: async () => {
      const params = buildStatsParams(days, startDate, endDate);
      const response = await apiClient.get(`/statistics/summary${params}`);
      return response.data;
    },
  });
}

export function useJobsOverTime(days = 30, startDate?: string, endDate?: string) {
  return useQuery<DailyJobCount[]>({
    queryKey: ['statistics', 'jobs-over-time', days, startDate, endDate],
    queryFn: async () => {
      const params = buildStatsParams(days, startDate, endDate);
      const response = await apiClient.get(`/statistics/jobs-over-time${params}`);
      return response.data;
    },
  });
}

export function useCostOverTime(days = 30, startDate?: string, endDate?: string) {
  return useQuery<DailyCost[]>({
    queryKey: ['statistics', 'cost-over-time', days, startDate, endDate],
    queryFn: async () => {
      const params = buildStatsParams(days, startDate, endDate);
      const response = await apiClient.get(`/statistics/cost-over-time${params}`);
      return response.data;
    },
  });
}

export function useFilamentByMaterial(days?: number, startDate?: string, endDate?: string) {
  return useQuery<FilamentByMaterial[]>({
    queryKey: ['statistics', 'filament-by-material', days, startDate, endDate],
    queryFn: async () => {
      const params = buildStatsParams(days, startDate, endDate);
      const response = await apiClient.get(`/statistics/filament-by-material${params}`);
      return response.data;
    },
  });
}

export function usePrinterUtilization(days?: number, startDate?: string, endDate?: string) {
  return useQuery<PrinterUtilization[]>({
    queryKey: ['statistics', 'printer-utilization', days, startDate, endDate],
    queryFn: async () => {
      const params = buildStatsParams(days, startDate, endDate);
      const response = await apiClient.get(`/statistics/printer-utilization${params}`);
      return response.data;
    },
  });
}
