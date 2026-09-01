import { client } from '@/services/api/httpClient';
import type { PrinterQueueSummaryDto } from '@/types/api';

/**
 * Extracted so `useQueueSummariesFleet.ts` (statically reachable from
 * `App.tsx` via `QueueRealtimeBridge.tsx`) doesn't pull in the full
 * `ApiClient` monolith. See issue #2343.
 */
export async function getPrinterQueueSummaries(signal?: AbortSignal): Promise<PrinterQueueSummaryDto[]> {
  const response = await client.get<PrinterQueueSummaryDto[]>(
    '/job-queue-analytics/printer-summaries',
    { signal },
  );
  return response.data ?? [];
}
