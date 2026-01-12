/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import clsx from 'clsx';

export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'subtle' | 'success' | 'tab' | 'toggle';
export type ButtonSize = 'sm' | 'md' | 'lg';

export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  loading?: boolean;
  iconLeft?: React.ReactNode;
  iconRight?: React.ReactNode;
  iconCenter?: React.ReactNode;
  children?: React.ReactNode;
}

const variantClasses: Record<ButtonVariant, string> = {
  primary: 'bg-pf-accent-bg hover:bg-pf-accent-hover text-white border border-pf-accent-bg hover:border-pf-accent-hover shadow-md font-semibold',
  secondary: 'bg-pf-bg-2 hover:bg-pf-bg-1 text-pf-text-primary border border-pf-border-light hover:border-pf-border',
  danger: 'bg-pf-error hover:bg-pf-error-hover text-white border border-pf-error-border shadow-md font-semibold',
  subtle: 'bg-transparent hover:bg-pf-bg-1 text-pf-text-secondary border border-transparent',
  success: 'bg-pf-success-bg hover:bg-pf-success-hover text-white border border-pf-success shadow-md font-semibold',
  tab: 'bg-transparent text-pf-text-muted border-b-2 border-transparent hover:text-pf-text-primary focus:ring-0',
  toggle: 'bg-transparent text-pf-text-secondary hover:text-pf-text-primary border-transparent'
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
    children,
    ...rest
  },
  ref
) {
  return (
    <button
      ref={ref}
      className={clsx(
        'rounded-sm font-medium inline-flex items-center gap-2 whitespace-nowrap transition-all duration-200 disabled:opacity-50 disabled:cursor-not-allowed focus:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-pf-accent shadow-sm',
        variantClasses[variant],
        sizeClasses[size],
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
