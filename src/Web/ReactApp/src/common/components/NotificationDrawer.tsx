import { useEffect } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import { CloseIcon, CheckIcon } from '@/common/components/icons/MdiIcons';
import { useNotifications, useMarkNotificationAsRead, useMarkAllNotificationsAsRead, useDeleteNotification } from '@/common/hooks/useApi';
import { NotificationDto, NotificationType } from '@/types/api';
import { formatDistanceToNow } from 'date-fns';

interface NotificationDrawerProps {
  isOpen: boolean;
  onClose: () => void;
}

export function NotificationDrawer({ isOpen, onClose }: NotificationDrawerProps) {
  const { data: notifications = [], refetch } = useNotifications({ limit: 50 });
  const markAsReadMutation = useMarkNotificationAsRead();
  const markAllAsReadMutation = useMarkAllNotificationsAsRead();
  const deleteNotificationMutation = useDeleteNotification();

  // Refetch when drawer opens
  useEffect(() => {
    if (isOpen) {
      refetch();
    }
  }, [isOpen, refetch]);

  const handleNotificationClick = async (notification: NotificationDto) => {
    if (!notification.isRead) {
      await markAsReadMutation.mutateAsync(notification.id);
    }
    // Navigate to relevant resource if needed
    if (notification.jobId) {
      // Could navigate to job details page
      // navigate(`/queue/${notification.jobId}`);
    }
  };

  const handleMarkAllAsRead = async () => {
    const unreadIds = notifications.filter(n => !n.isRead).map(n => n.id);
    if (unreadIds.length > 0) {
      await markAllAsReadMutation.mutateAsync(unreadIds);
    }
  };

  const handleDelete = async (notificationId: string, e: React.MouseEvent) => {
    e.stopPropagation();
    await deleteNotificationMutation.mutateAsync(notificationId);
  };

  const getNotificationIcon = (type: NotificationType) => {
    switch (type) {
      case NotificationType.JobCompleted:
        return '✅';
      case NotificationType.JobFailed:
        return '❌';
      case NotificationType.JobStarted:
        return '▶️';
      case NotificationType.JobPaused:
        return '⏸️';
      case NotificationType.JobResumed:
        return '▶️';
      case NotificationType.QueueAlert:
        return '⚠️';
      case NotificationType.SystemAlert:
        return '🔔';
      default:
        return '📬';
    }
  };

  if (!isOpen) return null;

  return (
    <>
      {/* Backdrop */}
      <div 
        className="fixed inset-0 bg-black/50 z-40"
        onClick={onClose}
        aria-hidden="true"
      />

      {/* Drawer */}
      <div className={clsx(
        "fixed top-0 right-0 h-full w-full sm:w-96 bg-pf-bg-1 shadow-lg z-50 flex flex-col",
        "transition-transform duration-300 ease-in-out",
        isOpen ? "translate-x-0" : "translate-x-full"
      )}>
        {/* Header */}
        <div className="flex items-center justify-between p-4 border-b border-pf-border">
          <h2 className="text-lg font-bold text-pf-text-primary">Notifications</h2>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={onClose}
            aria-label="Close notifications"
          >
            <CloseIcon className="h-5 w-5" />
          </Button>
        </div>

        {/* Actions */}
        {notifications.length > 0 && notifications.some(n => !n.isRead) && (
          <div className="p-3 border-b border-pf-border">
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={handleMarkAllAsRead}
              iconLeft={<CheckIcon className="h-4 w-4" />}
              disabled={markAllAsReadMutation.isPending}
            >
              Mark all as read
            </Button>
          </div>
        )}

        {/* Notification List */}
        <div className="flex-1 overflow-y-auto">
          {notifications.length === 0 ? (
            <div className="flex flex-col items-center justify-center h-full p-8 text-center">
              <span className="text-4xl mb-2">📭</span>
              <p className="text-pf-text-secondary">No notifications</p>
            </div>
          ) : (
            <div className="divide-y divide-pf-border">
              {notifications.map((notification) => (
                <div
                  key={notification.id}
                  className={clsx(
                    "p-4 cursor-pointer hover:bg-pf-bg-0 transition-colors",
                    !notification.isRead && "bg-pf-accent-bg/10"
                  )}
                  onClick={() => handleNotificationClick(notification)}
                >
                  <div className="flex items-start gap-3">
                    {/* Icon */}
                    <span className="text-2xl flex-shrink-0">
                      {getNotificationIcon(notification.type)}
                    </span>

                    {/* Content */}
                    <div className="flex-1 min-w-0">
                      <div className="flex items-start justify-between gap-2">
                        <h3 className={clsx(
                          "text-sm font-medium",
                          !notification.isRead ? "text-pf-text-primary" : "text-pf-text-secondary"
                        )}>
                          {notification.subject}
                        </h3>
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          onClick={(e) => handleDelete(notification.id, e)}
                          className="flex-shrink-0"
                          aria-label="Delete notification"
                        >
                          <CloseIcon className="h-4 w-4" />
                        </Button>
                      </div>
                      <p className="text-xs text-pf-text-tertiary mt-1 line-clamp-2">
                        {notification.body}
                      </p>
                      <div className="flex items-center gap-2 mt-2">
                        <span className="text-xs text-pf-text-muted">
                          {formatDistanceToNow(new Date(notification.createdAt), { addSuffix: true })}
                        </span>
                        {!notification.isRead && (
                          <span className="h-2 w-2 rounded-full bg-pf-accent" title="Unread" />
                        )}
                      </div>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </>
  );
}
