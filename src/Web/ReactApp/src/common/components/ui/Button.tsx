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
  primary: 'bg-gradient-to-b from-pf-gradient-primary-start to-pf-gradient-primary-end text-pf-text-primary border border-pf-border-light hover:from-pf-gradient-secondary-start hover:to-pf-gradient-secondary-end hover:border-pf-accent-2',
  secondary: 'bg-gradient-to-b from-pf-gradient-gray-start to-pf-gradient-gray-end text-white border border-pf-border-medium hover:from-pf-gradient-gray-dark-start hover:to-pf-gradient-gray-dark-end',
  danger: 'bg-pf-error hover:bg-pf-error text-white border border-pf-error-border',
  subtle: 'bg-transparent hover:bg-pf-bg-1 text-pf-text-secondary border border-transparent',
  success: 'bg-gradient-to-b from-pf-gradient-success-start to-pf-gradient-success-end text-white border border-pf-success hover:bg-pf-success-hover',
  tab: 'bg-transparent text-pf-text-muted border-b-2 border-transparent hover:text-pf-text-primary focus:ring-0',
  toggle: 'bg-transparent text-pf-text-secondary hover:text-pf-text-primary border-transparent'
};

const sizeClasses: Record<ButtonSize, string> = {
  sm: 'text-xs px-2 py-1',
  md: 'text-sm px-4 py-2',
  lg: 'text-base px-6 py-3'
};

export const Button: React.FC<ButtonProps> = ({
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
}) => {
  // Icon-only button with iconCenter
  if (iconCenter) {
    return (
      <button
        className={clsx(
          'rounded-sm font-medium inline-flex items-center justify-center whitespace-nowrap transition-all duration-200 disabled:opacity-50 disabled:cursor-not-allowed focus:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-pf-accent shadow-sm',
          variantClasses[variant],
          sizeClasses[size],
          className
        )}
        disabled={disabled || loading}
        {...rest}
      >
        <span className="flex items-center" aria-hidden>{loading ? 'Loading...' : iconCenter}</span>
      </button>
    );
  }

  // Regular button with text and optional left/right icons
  return (
    <button
      className={clsx(
        'rounded-sm font-medium inline-flex items-center gap-2 whitespace-nowrap transition-all duration-200 disabled:opacity-50 disabled:cursor-not-allowed focus:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-pf-accent shadow-sm',
        variantClasses[variant],
        sizeClasses[size],
        className
      )}
      disabled={disabled || loading}
      {...rest}
    >
      {iconLeft && <span className="flex items-center" aria-hidden>{iconLeft}</span>}
      {children && <span>{loading ? 'Please wait…' : children}</span>}
      {iconRight && <span className="flex items-center" aria-hidden>{iconRight}</span>}
    </button>
  );
};

export default Button;
