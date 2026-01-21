import { useQuery } from '@tanstack/react-query';
import { maintenanceService } from '@/services/maintenanceService';

export function usePrinterUptime() {
  return useQuery({
    queryKey: ['maintenance', 'printerUptime'],
    queryFn: async () => {
      // Example API call, replace with real endpoint
      return maintenanceService.getPrinterUptime();
    },
  });
}
