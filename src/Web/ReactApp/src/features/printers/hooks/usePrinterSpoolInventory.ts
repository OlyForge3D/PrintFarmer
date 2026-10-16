import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';

/** Reuse the printer-aware inventory path; coverage can contain estimated consumption. */
export function usePrinterSpoolInventory(printerId: string, hasAssignedSpools: boolean) {
  return useQuery({
    queryKey: ['printer-spools', printerId],
    queryFn: () => apiClient.getPrinterSpools(printerId),
    enabled: hasAssignedSpools,
    staleTime: 30_000,
    refetchInterval: 60_000,
    retry: false,
  });
}
