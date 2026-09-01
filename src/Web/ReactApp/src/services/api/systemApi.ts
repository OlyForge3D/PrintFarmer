import { client } from '@/services/api/httpClient';
import type { SystemInfo } from '@/types/api';

/**
 * Extracted so `SystemPulsePill` (mounted eagerly via `Layout.tsx` ->
 * `FloatingControlBar.tsx`) doesn't pull in the full `ApiClient` monolith.
 * `ApiClient.getSystemInfo` remains in `api.ts` for the lazy
 * `SystemStatusPage` consumer — this is a verbatim duplicate. See issue #2343.
 */
export async function getSystemInfo(): Promise<SystemInfo> {
  const response = await client.get<SystemInfo>('/system/info');
  return response.data;
}
