import { useQuery } from '@tanstack/react-query';
import { maintenanceService } from '@/services/maintenanceService';

export function useMaintenanceCostAnalysis() {
  return useQuery(['maintenance', 'costAnalysis'], async () => {
    // Example API call, replace with real endpoint
    return maintenanceService.getCostAnalysis();
  });
}
