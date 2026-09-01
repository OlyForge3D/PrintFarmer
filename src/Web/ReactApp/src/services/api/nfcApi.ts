import { client } from '@/services/api/httpClient';
import type { SpoolmanSpool } from '@/types/api';
import type { NfcLinkRequest, NfcLinkResponse } from '@/features/nfc/types';

/**
 * NFC pairing API used by `NfcPairingModal.tsx`, which is statically imported
 * by `Layout.tsx` (mounted for every authenticated route). This module calls
 * the shared axios client directly rather than delegating to the `ApiClient`
 * monolith, keeping it out of that monolith's eager import graph. See issue
 * #2343.
 *
 * `getSpools` is duplicated here (rather than imported from `api.ts`) because
 * the `ApiClient` class itself is not tree-shakeable — importing any of its
 * methods pulls in the whole class. The canonical `ApiClient.getSpools` stays
 * in `api.ts` for its many other (lazy) consumers.
 */

export async function linkNfcTag(request: NfcLinkRequest): Promise<NfcLinkResponse> {
  const response = await client.post('/nfc/link', request);
  return response.data as NfcLinkResponse;
}

export async function getSpools(params?: {
  limit?: number;
  offset?: number;
  sort?: string;
  search?: string;
  material?: string;
  vendor?: string;
  location?: string;
  allowArchived?: boolean;
  signal?: AbortSignal;
}): Promise<{ items: SpoolmanSpool[]; totalCount: number }> {
  const queryParams: Record<string, string | number | boolean> = {};
  if (params?.limit && params.limit > 0) queryParams.limit = params.limit;
  if (params?.offset && params.offset > 0) queryParams.offset = params.offset;
  if (params?.sort) queryParams.sort = params.sort;
  if (params?.search) queryParams.search = params.search;
  if (params?.material) queryParams.material = params.material;
  if (params?.vendor) queryParams.vendor = params.vendor;
  if (params?.location) queryParams.location = params.location;
  if (params?.allowArchived) queryParams.allowArchived = true;

  const response = await client.get('/spoolman/spools', {
    params: Object.keys(queryParams).length > 0 ? queryParams : undefined,
    signal: params?.signal,
  });
  const data = response.data;

  // Handle the new paginated response format { items, totalCount }
  if (data && typeof data === 'object' && !Array.isArray(data) && 'items' in data) {
    const result = data as { items: SpoolmanSpool[]; totalCount: number };
    return {
      items: Array.isArray(result.items) ? result.items : [],
      totalCount: typeof result.totalCount === 'number' ? result.totalCount : 0,
    };
  }

  // Fallback for plain array response (backward compatibility)
  const items = Array.isArray(data) ? (data as SpoolmanSpool[]) : [];
  const offset = params?.offset ?? 0;
  return { items, totalCount: Math.max(items.length, offset + items.length) };
}
