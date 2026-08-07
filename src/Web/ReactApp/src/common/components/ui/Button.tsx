/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import clsx from 'clsx';
import { hasRadiusOverride } from '@/common/components/ui/radius-classes';

export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'subtle' | 'ghost' | 'success' | 'tab' | 'toggle' | 'link' | 'unstyled';
export type ButtonSize = 'sm' | 'md' | 'lg';

export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  loading?: boolean;
  iconLeft?: React.ReactNode;
  iconRight?: React.ReactNode;
  iconCenter?: React.ReactNode;
  /** For tab variant: whether this tab is currently active/selected */
  active?: boolean;
  children?: React.ReactNode;
}

const variantClasses: Record<ButtonVariant, string> = {
  primary: 'bg-[var(--pf-button-primary-bg)] enabled:hover:bg-[var(--pf-button-primary-hover)] text-[var(--pf-on-accent)] border border-[var(--pf-button-primary-border)] font-semibold',
  secondary: 'bg-pf-bg-2 enabled:hover:bg-pf-bg-1 text-pf-text-primary border border-pf-border-light enabled:hover:border-pf-border',
  danger: 'bg-[var(--pf-button-danger-bg)] enabled:hover:bg-[var(--pf-button-danger-hover)] text-[var(--pf-on-danger)] border border-[var(--pf-button-danger-border)] font-semibold',
  // `subtle` declares only the border *width*. Its paint — surface, hover overlay,
  // text colour and border colour — lives in the components layer, for the same
  // reason as ghost below. See #1102.
  subtle: 'border',
  // Ghost deliberately declares no utilities at all. Its defaults — transparent
  // surface, inherited text colour, transparent border, no shadow, and the hover
  // overlay — live in the components layer (`styles/controls.css`), keyed off
  // `[data-pf-variant='ghost']`, so that anything a caller passes through
  // `className` wins on layer order instead of losing on source order. See #1087.
  ghost: '',
  success: 'bg-[var(--pf-button-success-bg)] enabled:hover:bg-[var(--pf-button-success-hover)] text-[var(--pf-button-success-text)] border border-[var(--pf-button-success-border)] font-semibold',
  // tab/toggle/link keep only their structural utilities; their paint lives in the
  // components layer alongside subtle's and ghost's. See #1102.
  tab: 'border-b-2 focus:ring-0',
  toggle: '',
  link: 'enabled:hover:underline px-0 py-0',
  unstyled: '' // No default styles - fully controlled by className prop
};

const sizeClasses: Record<ButtonSize, string> = {
  sm: 'text-xs px-2 py-1',
  md: 'text-sm px-4 py-2',
  lg: 'text-base px-6 py-3'
};

export const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  {
    variant = 'primary',
    size = 'md',
    loading = false,
    disabled,
    className,
    iconLeft,
    iconRight,
    iconCenter,
    active,
    children,
    ...rest
  },
  ref
) {
  // The tab variant's idle/active paint used to be emitted here as utilities,
  // which put it in `@layer utilities` alongside caller classes where it won on
  // source order — the #1102 defect. Colour and the active underline now live in
  // `@layer components` (styles/controls.css) keyed off `data-pf-variant` and
  // `data-pf-active`; only the active-state hook is emitted from here.
  const isActiveTab = variant === 'tab' && active === true;

  // Link variant should not apply size padding classes
  // Unstyled variant should not apply any base styles
  // Ghost variant should not apply ring-offset (needs to blend into any background)
  const applySizeClasses = variant !== 'link' && variant !== 'unstyled';
  const applyBaseStyles = variant !== 'unstyled';
  const applyRingOffset = variant !== 'ghost' && variant !== 'link' && variant !== 'unstyled';
  const defaultRadiusClass = variant === 'tab' ? 'rounded-none' : 'rounded-xs';

  return (
    <button
      ref={ref}
      data-pf-button
      data-pf-variant={variant}
      data-pf-active={isActiveTab ? '' : undefined}
      className={clsx(
        applyBaseStyles &&
          'font-medium inline-flex items-center justify-center gap-2 whitespace-nowrap transition-all duration-200 enabled:cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
        applyBaseStyles && !hasRadiusOverride(className) && defaultRadiusClass,
        applyRingOffset && 'focus-visible:ring-offset-2',
        variantClasses[variant],
        applySizeClasses && sizeClasses[size],
        // center icon style when iconCenter provided
        iconCenter && 'justify-center',
        className
      )}
      disabled={disabled || loading}
      {...rest}
    >
      {iconLeft && <span className="flex items-center" aria-hidden>{iconLeft}</span>}
      {iconCenter ? (
        <>
          <span className="flex items-center" aria-hidden>{iconCenter}</span>
          {loading && <span>Loading...</span>}
        </>
      ) : (
        <>
          {children && <span className="min-w-0">{loading ? 'Please wait…' : children}</span>}
          {iconRight && <span className="flex items-center" aria-hidden>{iconRight}</span>}
        </>
      )}
    </button>
  );
});

Button.displayName = 'Button';

export default Button;
