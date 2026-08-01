import type { ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useSettingsHeaderSlot } from '@/features/settings/components/settingsHeaderSlotContext';

export interface SettingsHeaderPortalProps {
  children: ReactNode;
}

/**
 * Renders `children` into the settings shell's page-header actions row.
 *
 * Falls back to rendering them inline when there is no slot — mounted
 * standalone, or outside the shell entirely. A portal that renders `null`
 * without a target would silently delete a working control, which is a
 * capability regression dressed up as a layout decision. Degrading in place
 * costs nothing and keeps the control reachable wherever the page is used.
 */
export function SettingsHeaderPortal({ children }: SettingsHeaderPortalProps) {
  const slot = useSettingsHeaderSlot();
  if (!slot) {
    return <>{children}</>;
  }
  return createPortal(children, slot);
}
