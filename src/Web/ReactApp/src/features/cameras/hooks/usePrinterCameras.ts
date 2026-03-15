import { useQuery } from '@tanstack/react-query';
import { cameraService } from '@/services/cameraService';

export function usePrinterCameras(printerId: string | undefined) {
  return useQuery({
    queryKey: ['cameras', 'by-printer', printerId],
    queryFn: () => cameraService.getCamerasByPrinter(printerId!),
    enabled: !!printerId,
    staleTime: 30_000,
  });
}
