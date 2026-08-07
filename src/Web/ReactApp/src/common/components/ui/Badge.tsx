import React from 'react';
import clsx from 'clsx';
import { hasRadiusOverride } from '@/common/components/ui/radius-classes';

export type BadgeVariant = 'default' | 'primary' | 'success' | 'warning' | 'error' | 'info';
export type BadgeSize = 'sm' | 'md';
export type BadgeShape = 'status' | 'tag';

export interface BadgeProps {
  /** The content to display inside the badge */
  children: React.ReactNode;
  /** Visual variant */
  variant?: BadgeVariant;
  /** Size of the badge */
  size?: BadgeSize;
  /**
   * What the badge *is*, which decides its radius. `status` is the default and
   * follows the 2px status-pill contract; `tag` renders a fully round tag chip
   * and signs the waiver for it. See DESIGN-LANGUAGE "Badges / Status Pills".
   */
  shape?: BadgeShape;
  /** Show as a dot/indicator only (no text) */
  dot?: boolean;
  /** Additional className */
  className?: string;
}

const variantClasses: Record<BadgeVariant, string> = {
  default: 'bg-pf-bg-2 text-pf-text-secondary border-pf-border',
  primary: 'bg-pf-accent/15 text-pf-accent border-pf-accent/30',
  success: 'bg-pf-success-bg text-pf-success-text border-pf-success/30',
  warning: 'bg-pf-warning-bg text-pf-warning-text border-pf-warning/30',
  error: 'bg-pf-error-bg text-pf-error-text border-pf-error/30',
  info: 'bg-pf-accent/15 text-pf-accent border-pf-accent/30',
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
  shape = 'status',
  dot = false,
  className,
}) => {
  const overridden = hasRadiusOverride(className);

  if (dot) {
    return (
      <span
        className={clsx(
          // `aspect-square` is redundant next to the w/h pair below, but those
          // arrive through a lookup the radius lint cannot read — and it is
          // true of every size, so stating it costs nothing and documents that
          // the dot really is a dot.
          'inline-block aspect-square',
          !overridden && 'rounded-full',
          dotSizeClasses[size],
          dotVariantClasses[variant],
          className
        )}
        aria-hidden="true"
      />
    );
  }

  // DESIGN-LANGUAGE "Badges / Status Pills": status pills are --pf-radius-xs,
  // tag chips are fully round. Tag chips declare the waiver here rather than at
  // the call site, because the call site cannot: this component does not spread
  // arbitrary DOM props.
  const isTag = shape === 'tag';

  return (
    <span
      className={clsx(
        'inline-flex items-center font-medium border',
        !overridden && (isTag ? 'rounded-full' : 'rounded-xs'),
        variantClasses[variant],
        sizeClasses[size],
        className
      )}
      data-pf-radius={isTag && !overridden ? 'full' : undefined}
    >
      {children}
    </span>
  );
};

export default Badge;
