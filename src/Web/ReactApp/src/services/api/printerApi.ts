import { client } from '@/services/api/httpClient';
import type { Printer, PrinterFast } from '@/types/api';

/**
 * Printer domain API — calls the shared axios client directly (see
 * `services/api/httpClient.ts`) rather than delegating to the `ApiClient`
 * monolith. Currently holds only the subset needed by eager consumers
 * (`QueueRealtimeBridge.tsx`, statically mounted by `App.tsx`); the rest of
 * the printer surface still lives on `printerService.ts` pending its own
 * migration. See issue #2343.
 */

export async function getPrinters(includeDisabled?: boolean): Promise<Printer[]> {
  // Get lightweight list of all printers
  const params = includeDisabled ? { includeDisabled: true } : undefined;
  const response = await client.get<PrinterFast[]>('/printers', { params });
  // Cast to Printer[] for compatibility; fast objects are subset of Printer
  return response.data as unknown as Printer[];
}
