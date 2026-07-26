import type { ReactNode } from 'react';
import clsx from 'clsx';

export interface AdminEmptyProps {
  /** Icon shown above the title. Keep it neutral — an empty state is not a warning. */
  icon?: ReactNode;
  /** Short headline. Should describe what's missing, not the fix. */
  title: string;
  /**
   * One-sentence supporting text. Explain *why* it's empty when the answer is
   * useful ("No printers configured yet") — otherwise omit.
   */
  description?: string;
  /**
   * Primary call to action, typically a `<Button variant="primary">`. Optional —
   * an empty state doesn't need an action if there's nothing the user can do.
   */
  action?: ReactNode;
  /** Optional secondary action (a link or subtle Button) rendered next to `action`. */
  secondaryAction?: ReactNode;
  /**
   * Density preset. `default` gives generous padding for full-page empty states;
   * `compact` fits inside a card or a modal.
   */
  size?: 'default' | 'compact';
  className?: string;
}

/**
 * Unified empty state for admin surfaces. Replaces the four existing bespoke
 * empty-state layouts spread across the admin pages.
 *
 * Structural pattern: icon → headline → description → actions. All optional
 * except the headline.
 */
export function AdminEmpty({
  icon,
  title,
  description,
  action,
  secondaryAction,
  size = 'default',
  className,
}: AdminEmptyProps) {
  const paddingClass = size === 'compact' ? 'py-8 px-4' : 'py-16 px-6';

  return (
    <div
      role="status"
      className={clsx(
        'flex flex-col items-center justify-center text-center',
        paddingClass,
        className,
      )}
    >
      {icon && (
        <div
          className={clsx(
            'text-pf-text-tertiary opacity-50',
            size === 'compact' ? 'mb-3' : 'mb-4',
          )}
          aria-hidden="true"
        >
          {icon}
        </div>
      )}
      <h3 className="text-base font-semibold text-pf-text-primary">{title}</h3>
      {description && (
        <p className="mt-1.5 text-sm text-pf-text-secondary max-w-md">
          {description}
        </p>
      )}
      {(action || secondaryAction) && (
        <div
          className={clsx(
            'flex flex-wrap items-center justify-center gap-2',
            size === 'compact' ? 'mt-4' : 'mt-6',
          )}
        >
          {action}
          {secondaryAction}
        </div>
      )}
    </div>
  );
}

export default AdminEmpty;
