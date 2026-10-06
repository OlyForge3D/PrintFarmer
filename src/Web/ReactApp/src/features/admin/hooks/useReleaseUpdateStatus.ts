import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { client } from '@/services/api/httpClient';
import type { ApplicationReleaseUpdateStatusDto } from '@/types/releaseUpdates';

export const RELEASE_UPDATE_STATUS_QUERY_KEY = ['admin', 'release-updates'] as const;

/** Client polling cadence. The server answers from its cached check, so this never reaches GitHub. */
export const RELEASE_UPDATE_POLL_INTERVAL_MS = 5 * 60_000;

async function fetchReleaseUpdateStatus(signal?: AbortSignal): Promise<ApplicationReleaseUpdateStatusDto> {
  const response = await client.get<ApplicationReleaseUpdateStatusDto>('/admin/release-updates', { signal });
  return response.data;
}

/**
 * Cached application release update status for farm admins. Pass `enabled: false` for
 * non-admins so no request is ever made; cached data is hidden when access is removed.
 */
export function useReleaseUpdateStatus(
  options?: { enabled?: boolean },
): UseQueryResult<ApplicationReleaseUpdateStatusDto | undefined> {
  const enabled = options?.enabled ?? true;
  return useQuery<ApplicationReleaseUpdateStatusDto, Error, ApplicationReleaseUpdateStatusDto | undefined>({
    queryKey: RELEASE_UPDATE_STATUS_QUERY_KEY,
    queryFn: ({ signal }) => fetchReleaseUpdateStatus(signal),
    enabled,
    staleTime: 60_000,
    refetchInterval: enabled ? RELEASE_UPDATE_POLL_INTERVAL_MS : false,
    refetchOnWindowFocus: false,
    retry: false,
    select: enabled ? undefined : () => undefined,
  });
}
