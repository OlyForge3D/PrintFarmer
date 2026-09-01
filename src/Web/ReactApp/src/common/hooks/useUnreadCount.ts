import { useQuery } from '@tanstack/react-query';
import type { UseQueryOptions } from '@tanstack/react-query';
import { getUnreadCount } from '@/services/api/notificationsApi';
import { queryKeys } from '@/common/hooks/queryKeys';
import type { ApiError } from '@/types/api';

type QueryOptions<TData, TError = ApiError> = Omit<UseQueryOptions<TData, TError>, 'queryKey' | 'queryFn'>;

/**
 * Split out of `useApi.ts` because `NotificationBell` (mounted eagerly in
 * `Layout.tsx`) needs this hook without pulling in the full `ApiClient`
 * monolith that `useApi.ts` otherwise imports. See issue #2343.
 */
export function useUnreadCount(options?: QueryOptions<number>) {
  return useQuery({
    queryKey: queryKeys.unreadCount,
    queryFn: () => getUnreadCount(),
    staleTime: 10_000, // 10s
    ...options,
  });
}
