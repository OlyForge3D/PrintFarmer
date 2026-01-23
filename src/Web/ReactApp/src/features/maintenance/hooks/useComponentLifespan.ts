import { useQuery } from '@tanstack/react-query';
import { maintenanceService } from '@/services/maintenanceService';

export function useComponentLifespan() {
  return useQuery({
    queryKey: ['maintenance', 'componentLifespan'],
    queryFn: async () => {
      // Example API call, replace with real endpoint
      return maintenanceService.getComponentLifespan();
    },
  });
}
