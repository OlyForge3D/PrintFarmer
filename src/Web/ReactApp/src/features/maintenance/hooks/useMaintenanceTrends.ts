import { useQuery } from '@tanstack/react-query';
import { maintenanceService } from '@/services/maintenanceService';

export function useMaintenanceTrends() {
  return useQuery(['maintenance', 'trends'], async () => {
    // Example API call, replace with real endpoint
    return maintenanceService.getTrends();
  });
}
