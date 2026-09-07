import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { client } from '@/services/api/httpClient';
import type { AdminOverviewDto } from '@/types/adminOverview';

/**
 * React-Query key for the Admin Control Center overview. Exported so tests and
 * sibling admin views (e.g. Ctrl+K palette) can invalidate the same cache entry.
 */
export const ADMIN_OVERVIEW_QUERY_KEY = ['admin', 'overview'] as const;

async function fetchAdminOverview(signal?: AbortSignal): Promise<AdminOverviewDto> {
  const response = await client.get<AdminOverviewDto>('/admin/overview', { signal });
  return response.data;
}

/**
 * Fetch the Admin Control Center overview snapshot.
 *
 * The server aggregates existing health checks with an 8-second internal timeout and
 * never 500s on probe failure — a failure surfaces as `status: "Unknown"` on the
 * relevant tile. We still surface network-level failures through React-Query so the
 * hub can render `AdminError` with a working retry.
 *
 * `refetchOnWindowFocus` is on because the hub is what operators reopen when they
 * suspect something is wrong; stale-under-focus is the wrong default for it.
 */
export function useAdminOverview(
  options?: { enabled?: boolean },
): UseQueryResult<AdminOverviewDto | undefined> {
  return useQuery<AdminOverviewDto, Error, AdminOverviewDto | undefined>({
    queryKey: ADMIN_OVERVIEW_QUERY_KEY,
    queryFn: ({ signal }) => fetchAdminOverview(signal),
    staleTime: 30_000,
    refetchOnWindowFocus: true,
    enabled: options?.enabled ?? true,
    // React Query retains cached data when a query becomes disabled. Do not let
    // a principal who loses overview access render that old snapshot.
    select: options?.enabled === false ? () => undefined : undefined,
  });
}
