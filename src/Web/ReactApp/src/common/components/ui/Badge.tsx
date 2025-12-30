import React from 'react';
import clsx from 'clsx';

export type BadgeVariant = 'default' | 'primary' | 'success' | 'warning' | 'error' | 'info';
export type BadgeSize = 'sm' | 'md';

export interface BadgeProps {
  /** The content to display inside the badge */
  children: React.ReactNode;
  /** Visual variant */
  variant?: BadgeVariant;
  /** Size of the badge */
  size?: BadgeSize;
  /** Show as a dot/indicator only (no text) */
  dot?: boolean;
  /** Additional className */
  className?: string;
}

const variantClasses: Record<BadgeVariant, string> = {
  default: 'bg-pf-bg-2 text-pf-text-secondary border-pf-border',
  primary: 'bg-pf-accent-bg text-pf-accent border-pf-accent/30',
  success: 'bg-pf-success-bg text-pf-success-text border-pf-success/30',
  warning: 'bg-pf-warning-bg text-pf-warning-text border-pf-warning/30',
  error: 'bg-pf-error-bg text-pf-error-text border-pf-error/30',
  info: 'bg-pf-accent-bg text-pf-accent border-pf-accent/30',
};

const sizeClasses: Record<BadgeSize, string> = {
  sm: 'text-xs px-1.5 py-0.5',
  md: 'text-sm px-2 py-0.5',
};

const dotSizeClasses: Record<BadgeSize, string> = {
  sm: 'w-2 h-2',
  md: 'w-2.5 h-2.5',
};

const dotVariantClasses: Record<BadgeVariant, string> = {
  default: 'bg-pf-text-tertiary',
  primary: 'bg-pf-accent',
  success: 'bg-pf-success',
  warning: 'bg-pf-warning',
  error: 'bg-pf-error',
  info: 'bg-pf-accent',
};

export const Badge: React.FC<BadgeProps> = ({
  children,
  variant = 'default',
  size = 'sm',
  dot = false,
  className,
}) => {
  if (dot) {
    return (
      <span
        className={clsx(
          'inline-block rounded-full',
          dotSizeClasses[size],
          dotVariantClasses[variant],
          className
        )}
        aria-hidden="true"
      />
    );
  }

  return (
    <span
      className={clsx(
        'inline-flex items-center font-medium rounded-full border',
        variantClasses[variant],
        sizeClasses[size],
        className
      )}
    >
      {children}
    </span>
  );
};

export default Badge;
