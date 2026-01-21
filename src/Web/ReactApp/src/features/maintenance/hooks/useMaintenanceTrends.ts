import { useQuery } from '@tanstack/react-query';
import { maintenanceService } from '@/services/maintenanceService';

export function useMaintenanceTrends() {
  return useQuery<Array<{ date: string; printer: string; component: string; action: string; cost: number }>>({
    queryKey: ['maintenance', 'trends'],
    queryFn: async () => {
      return maintenanceService.getTrends();
    },
  });
}
