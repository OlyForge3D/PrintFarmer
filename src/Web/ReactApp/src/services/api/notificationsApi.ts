import { client } from '@/services/api/httpClient';
import type { NotificationDto, UnreadCountResponse } from '@/types/api';

/**
 * Notification-related endpoints extracted so they can be used from the
 * eagerly-rendered `NotificationBell`/`NotificationDrawer` (mounted in
 * `Layout.tsx`) without pulling in the full `ApiClient` monolith. See
 * issue #2343.
 */
export async function getUnreadCount(): Promise<number> {
  const response = await client.get<UnreadCountResponse>('/notifications/unread/count');
  return response.data.unreadCount;
}

export async function getNotifications(limit?: number): Promise<NotificationDto[]> {
  const params = limit ? `?limit=${limit}` : '';
  const response = await client.get<NotificationDto[]>(`/notifications${params}`);
  return response.data || [];
}

export async function markNotificationAsRead(notificationId: string): Promise<void> {
  await client.put(`/notifications/${notificationId}/mark-read`);
}

export async function markMultipleNotificationsAsRead(notificationIds: string[]): Promise<void> {
  await client.put('/notifications/mark-read-batch', { notificationIds });
}

export async function deleteNotification(notificationId: string): Promise<void> {
  await client.delete(`/notifications/${notificationId}`);
}
