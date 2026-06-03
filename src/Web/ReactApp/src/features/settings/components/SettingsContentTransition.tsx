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
        'motion-safe:animate-[pf-settings-content-in_150ms_ease-out] motion-reduce:animate-none',
        className,
      )}
    >
      {children}
    </div>
  );
}
