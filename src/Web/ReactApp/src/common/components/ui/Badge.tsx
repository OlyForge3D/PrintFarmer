import React from 'react';
import clsx from 'clsx';

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

/**
 * True when `className` sets a border radius **unconditionally**.
 *
 * `clsx` concatenates, it does not merge, so a caller-supplied radius and the
 * default would both land on the element and the winner would be decided by
 * Tailwind's emission order rather than by intent. That order is
 * `full → lg → md → sm → xs`, so `rounded-xs` beats everything: once this
 * component defaulted to `rounded-xs`, a caller asking for `rounded-full`
 * silently got a 2px square. Standing down when the caller has an opinion makes
 * the override mean what it says, without pulling in `tailwind-merge`.
 *
 * Variant-prefixed radii (`md:rounded-md`, `hover:rounded-sm`) deliberately do
 * NOT count. They only bind inside their condition, so standing down for them
 * would leave the badge with no radius at all outside it — a guaranteed defect,
 * traded for a merely possible one. Keeping the default is safe in both
 * directions: Tailwind emits variants after the base utilities they shadow, so
 * the conditional still wins where it applies.
 *
 * Both `rounded*` utilities and the arbitrary-property form
 * (`[border-radius:12px]`) are recognised.
 *
 * The same trap exists on `Button` and `Card`, which hardcode their radii; left
 * alone here because it is pre-existing rather than introduced by this work, and
 * `Card` standing down changes rendering at ~67 call sites. Tracked in #1063.
 */
const hasRadiusOverride = (className?: string): boolean =>
  className !== undefined &&
  /(?:^|\s)!?(?:rounded(?:-\S+)?|\[border-radius:[^\]\s]+\])!?(?:\s|$)/.test(className);

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
  // arbitrary DOM props. That is also why ten existing tag chips still sign the
  // waiver by hand — they need `style`, ARIA, `title` or `onClick`, none of
  // which this closed prop set accepts. Raised by Vasquez; tracked in #1076.
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
