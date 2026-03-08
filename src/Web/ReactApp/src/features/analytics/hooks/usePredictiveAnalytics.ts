import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';

export interface PredictiveAlert {
  alertType: string;
  severity: string;
  message: string;
  recommendedAction: string;
}

export interface MaintenanceForecast {
  printerId: string;
  printerName: string;
  upcomingTasks: MaintenanceTask[];
}

export interface MaintenanceTask {
  taskName: string;
  estimatedDaysUntilDue: number;
  priority: string;
}

export function useActiveAlerts() {
  return useQuery<PredictiveAlert[]>({
    queryKey: ['predictive-analytics', 'active-alerts'],
    queryFn: async () => {
      const response = await apiClient.get('/predictive-analytics/active-alerts');
      return response.data;
    },
    staleTime: 60_000,
  });
}

export function useMaintenanceForecast(days?: number) {
  return useQuery<MaintenanceForecast[]>({
    queryKey: ['predictive-analytics', 'maintenance-forecast', days],
    queryFn: async () => {
      const params = days ? `?days=${days}` : '';
      const response = await apiClient.get(`/predictive-analytics/maintenance-forecast${params}`);
      return response.data;
    },
    staleTime: 300_000,
  });
}
