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

export function useStatisticsSummary(days?: number) {
  return useQuery<StatisticsSummary>({
    queryKey: ['statistics', 'summary', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/statistics/summary${params}`);
      return response.data;
    },
  });
}

export function useJobsOverTime(days = 30) {
  return useQuery<DailyJobCount[]>({
    queryKey: ['statistics', 'jobs-over-time', days],
    queryFn: async () => {
      const response = await apiClient.get(`/statistics/jobs-over-time?days=${days}`);
      return response.data;
    },
  });
}

export function useCostOverTime(days = 30) {
  return useQuery<DailyCost[]>({
    queryKey: ['statistics', 'cost-over-time', days],
    queryFn: async () => {
      const response = await apiClient.get(`/statistics/cost-over-time?days=${days}`);
      return response.data;
    },
  });
}

export function useFilamentByMaterial(days?: number) {
  return useQuery<FilamentByMaterial[]>({
    queryKey: ['statistics', 'filament-by-material', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/statistics/filament-by-material${params}`);
      return response.data;
    },
  });
}

export function usePrinterUtilization(days?: number) {
  return useQuery<PrinterUtilization[]>({
    queryKey: ['statistics', 'printer-utilization', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/statistics/printer-utilization${params}`);
      return response.data;
    },
  });
}
