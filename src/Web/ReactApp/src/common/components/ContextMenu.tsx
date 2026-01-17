import React, { useEffect, useRef, useEffectEvent } from 'react';
import { Button } from '@/common/components/ui/Button';

export interface ContextMenuItemAction {
  label: string;
  icon?: React.ComponentType<{ className?: string }>;
  onClick: () => void;
  variant?: 'default' | 'danger';
  disabled?: boolean;
}

export interface ContextMenuItemDivider {
  divider: true;
}

export type ContextMenuItem = ContextMenuItemAction | ContextMenuItemDivider;

interface ContextMenuProps {
  x: number;
  y: number;
  items: ContextMenuItem[];
  onClose: () => void;
}

/**
 * Context menu component displayed on right-click
 * Auto-closes on outside click or Escape key
 * Positions intelligently to avoid viewport overflow
 */
export function ContextMenu({ x, y, items, onClose }: ContextMenuProps) {
  const menuRef = useRef<HTMLDivElement>(null);

  // React 19: useEffectEvent for outside click handler without dependency on onClose
  const handleMouseDown = useEffectEvent((e: MouseEvent) => {
    if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
      onClose();
    }
  });

  // React 19: useEffectEvent for escape key handler
  const handleKeyDown = useEffectEvent((e: KeyboardEvent) => {
    if (e.key === 'Escape') {
      onClose();
    }
  });

  // Close menu on outside click - React 19: Simplified with useEffectEvent
  useEffect(() => {
    // Small delay to avoid immediate closing after open
    const timeoutId = setTimeout(() => {
      document.addEventListener('mousedown', handleMouseDown);
    }, 50);

    return () => {
      clearTimeout(timeoutId);
      document.removeEventListener('mousedown', handleMouseDown);
    };
  }, [handleMouseDown]);

  // Close menu on Escape key - React 19: Simplified with useEffectEvent
  useEffect(() => {
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [handleKeyDown]);

  // Adjust position to avoid off-screen rendering
  const menuWidth = 200; // Estimated menu width
  const menuHeight = items.length * 40; // Estimated item height
  const padding = 10;

  const finalX = Math.min(x, window.innerWidth - menuWidth - padding);
  const finalY = Math.min(y, window.innerHeight - menuHeight - padding);

  return (
    <div
      ref={menuRef}
      className="fixed bg-pf-bg-2 border border-pf-border rounded-lg shadow-lg z-50"
      style={{
        top: `${finalY}px`,
        left: `${finalX}px`,
        minWidth: '200px',
      }}
      role="menu"
      aria-label="Context menu"
    >
      {items.map((item, index) => {
        // Divider item
        if ('divider' in item && item.divider) {
          return (
            <div key={index}>
              <div className="h-px bg-pf-border my-1" role="separator" />
            </div>
          );
        }

        // Action item
        const actionItem = item as ContextMenuItemAction;
        return (
          <div key={index}>
            <Button
              onClick={() => {
                actionItem.onClick();
                onClose();
              }}
              disabled={actionItem.disabled}
              variant={actionItem.variant === 'danger' ? 'danger' : 'subtle'}
              size="sm"
              iconLeft={actionItem.icon && <actionItem.icon className="w-4 h-4" />}
              className="w-full justify-start px-4 py-2.5 text-sm rounded-none hover:bg-pf-bg-3"
              role="menuitem"
              type="button"
            >
              {actionItem.label}
            </Button>
          </div>
        );
      })}
    </div>
  );
}
