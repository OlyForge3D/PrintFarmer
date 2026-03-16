import { useState } from 'react';
import { Button } from '@/common/components/ui';
import { BellIcon } from '@/common/components/icons/MdiIcons';
import { useUnreadCount } from '@/common/hooks/useApi';
import { NotificationDrawer } from './NotificationDrawer';

export function NotificationBell() {
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);
  const { data: unreadCount = 0 } = useUnreadCount();

  return (
    <>
      <Button
        type="button"
        variant="unstyled"
        onClick={() => setIsDrawerOpen(true)}
        className="relative flex items-center text-pf-text-secondary hover:text-pf-text-primary cursor-pointer p-0"
        title={unreadCount > 0 ? `${unreadCount} unread notification${unreadCount !== 1 ? 's' : ''}` : 'Notifications'}
        aria-label={`Notifications${unreadCount > 0 ? ` (${unreadCount} unread)` : ''}`}
      >
        <BellIcon className="h-5 w-5" />
        {unreadCount > 0 && (
          <span className="absolute -top-1 -right-1 bg-pf-accent text-white text-[9px] font-bold rounded-full min-w-[16px] h-[16px] flex items-center justify-center leading-none">
            {unreadCount > 99 ? '99+' : unreadCount}
          </span>
        )}
      </Button>
      <NotificationDrawer isOpen={isDrawerOpen} onClose={() => setIsDrawerOpen(false)} />
    </>
  );
}
