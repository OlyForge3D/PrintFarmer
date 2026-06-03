import type { ReactNode } from 'react';
import clsx from 'clsx';

interface SettingsContentTransitionProps {
  children: ReactNode;
  className?: string;
}

export function SettingsContentTransition({ children, className }: SettingsContentTransitionProps) {
  return (
    <div
      className={clsx(
        'motion-safe:animate-[pf-settings-content-in_280ms_cubic-bezier(0.16,1,0.3,1)] motion-reduce:animate-none',
        className,
      )}
    >
      {children}
    </div>
  );
}
