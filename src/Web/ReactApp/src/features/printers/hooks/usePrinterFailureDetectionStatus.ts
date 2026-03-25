import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type {
  FailureDetectionMonitorStatusDto,
  FailureDetectionPrinterStatusDto,
} from '@/types/api';

const FAILURE_DETECTION_STATUS_QUERY_KEY = ['failure-detection-status'] as const;
const FAILURE_DETECTION_STATUS_STALE_MS = 15_000;
const FAILURE_DETECTION_STATUS_REFRESH_MS = 30_000;

export function usePrinterFailureDetectionStatus(printerId: string, enabled = true) {
  const query = useQuery<FailureDetectionMonitorStatusDto>({
    queryKey: FAILURE_DETECTION_STATUS_QUERY_KEY,
    queryFn: () => apiClient.getFailureDetectionStatus(),
    staleTime: FAILURE_DETECTION_STATUS_STALE_MS,
    refetchInterval: FAILURE_DETECTION_STATUS_REFRESH_MS,
    enabled,
  });

  const printerStatus = useMemo<FailureDetectionPrinterStatusDto | undefined>(
    () => query.data?.printers.find((printer) => printer.printerId === printerId),
    [printerId, query.data]
  );

  return {
    ...query,
    printerStatus,
  };
}
