import { useQuery } from '@tanstack/react-query';
import { maintenanceService } from '@/services/maintenanceService';

export function useMaintenanceCostAnalysis() {
  return useQuery({
    queryKey: ['maintenance', 'costAnalysis'],
    queryFn: async () => {
      // Example API call, replace with real endpoint
      return maintenanceService.getCostAnalysis();
    },
  });
}
