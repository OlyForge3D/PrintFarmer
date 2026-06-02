/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import clsx from 'clsx';

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
  primary: 'bg-[var(--pf-button-primary-bg)] enabled:hover:bg-[var(--pf-button-primary-hover)] text-[var(--pf-on-accent)] border border-[var(--pf-button-primary-border)] shadow-md font-semibold',
  secondary: 'bg-pf-bg-2 enabled:hover:bg-pf-bg-1 text-pf-text-primary border border-pf-border-light enabled:hover:border-pf-border',
  danger: 'bg-[var(--pf-button-danger-bg)] enabled:hover:bg-[var(--pf-button-danger-hover)] text-[var(--pf-on-danger)] border border-[var(--pf-button-danger-border)] shadow-md font-semibold',
  subtle: 'bg-transparent enabled:hover:bg-pf-bg-1 text-pf-text-secondary border border-transparent',
  ghost: '[background:none] enabled:hover:[background:rgba(255,255,255,0.10)] text-inherit border-transparent shadow-none',
  success: 'bg-pf-success-bg enabled:hover:bg-pf-success-hover text-white border border-pf-success shadow-md font-semibold',
  tab: 'bg-transparent border-b-2 border-transparent focus:ring-0 rounded-none',
  toggle: 'bg-transparent text-pf-text-secondary enabled:hover:text-pf-text-primary border-transparent',
  link: 'bg-transparent text-pf-primary enabled:hover:underline border-transparent px-0 py-0 shadow-none',
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
  // For tab variant, apply active styles
  const tabActiveClasses = variant === 'tab' && active
    ? 'border-pf-primary text-pf-text-primary'
    : variant === 'tab'
    ? 'text-pf-text-muted enabled:hover:text-pf-text-primary'
    : '';

  // Link variant should not apply size padding classes
  // Unstyled variant should not apply any base styles
  // Ghost variant should not apply shadow or ring-offset (needs to blend into any background)
  const applySizeClasses = variant !== 'link' && variant !== 'unstyled';
  const applyBaseStyles = variant !== 'unstyled';
  const applyShadow = variant !== 'ghost' && variant !== 'link' && variant !== 'unstyled';

  return (
    <button
      ref={ref}
      data-pf-button
      className={clsx(
        applyBaseStyles &&
          'rounded-xs font-medium inline-flex items-center justify-center gap-2 whitespace-nowrap transition-all duration-200 enabled:cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
        applyShadow && 'shadow-xs focus-visible:ring-offset-2',
        variantClasses[variant],
        applySizeClasses && sizeClasses[size],
        tabActiveClasses,
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
          {children && <span>{loading ? 'Please wait…' : children}</span>}
          {iconRight && <span className="flex items-center" aria-hidden>{iconRight}</span>}
        </>
      )}
    </button>
  );
});

Button.displayName = 'Button';

export default Button;
