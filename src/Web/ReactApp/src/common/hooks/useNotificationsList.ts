import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { UseQueryOptions } from '@tanstack/react-query';
import { toast } from 'sonner';
import {
  getNotifications,
  markNotificationAsRead,
  markMultipleNotificationsAsRead,
  deleteNotification,
} from '@/services/api/notificationsApi';
import { queryKeys } from '@/common/hooks/queryKeys';
import type { ApiError, NotificationDto } from '@/types/api';

type QueryOptions<TData, TError = ApiError> = Omit<UseQueryOptions<TData, TError>, 'queryKey' | 'queryFn'>;

/**
 * Split out of `useApi.ts` because `NotificationDrawer` (mounted eagerly via
 * `NotificationBell` in `Layout.tsx`) needs these hooks without pulling in
 * the full `ApiClient` monolith that `useApi.ts` otherwise imports. See
 * issue #2343.
 */
export function useNotifications(options?: QueryOptions<NotificationDto[]> & { limit?: number }) {
  const limit = options?.limit;
  return useQuery({
    queryKey: [...queryKeys.notifications, limit],
    queryFn: () => getNotifications(limit),
    staleTime: 30_000, // 30s
    ...options,
  });
}

export function useMarkNotificationAsRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (notificationId: string) => markNotificationAsRead(notificationId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.notifications });
      queryClient.invalidateQueries({ queryKey: queryKeys.unreadCount });
    },
    onError: (err: ApiError) => toast.error(`Failed to mark as read: ${err.message}`),
  });
}

export function useMarkAllNotificationsAsRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (notificationIds: string[]) => markMultipleNotificationsAsRead(notificationIds),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.notifications });
      queryClient.invalidateQueries({ queryKey: queryKeys.unreadCount });
      toast.success('All notifications marked as read');
    },
    onError: (err: ApiError) => toast.error(`Failed to mark as read: ${err.message}`),
  });
}

export function useDeleteNotification() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (notificationId: string) => deleteNotification(notificationId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.notifications });
      queryClient.invalidateQueries({ queryKey: queryKeys.unreadCount });
      toast.success('Notification deleted');
    },
    onError: (err: ApiError) => toast.error(`Failed to delete: ${err.message}`),
  });
}
