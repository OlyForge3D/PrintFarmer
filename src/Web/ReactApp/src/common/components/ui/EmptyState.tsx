import type { ReactNode } from 'react';
import clsx from 'clsx';

export interface EmptyStateProps {
  /** Optional icon displayed above the title */
  icon?: ReactNode;
  /** Primary message */
  title: string;
  /** Optional supporting text */
  description?: string;
  /** Optional call-to-action element (typically a Button) */
  action?: ReactNode;
  /** Additional CSS classes */
  className?: string;
}

export function EmptyState({ icon, title, description, action, className }: EmptyStateProps) {
  return (
    <div className={clsx('flex flex-col items-center justify-center py-12 text-center', className)}>
      {icon && (
        <div className="mb-4 text-pf-text-tertiary opacity-40">{icon}</div>
      )}
      <h3 className="text-lg font-semibold text-pf-text-primary">{title}</h3>
      {description && (
        <p className="mt-1 text-sm text-pf-text-secondary max-w-md">{description}</p>
      )}
      {action && <div className="mt-4">{action}</div>}
    </div>
  );
}
