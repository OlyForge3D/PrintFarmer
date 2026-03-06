import type { ComponentPropsWithoutRef } from 'react';

const sizeClasses = {
  sm: 'w-4 h-4',
  md: 'w-6 h-6',
  lg: 'w-8 h-8',
} as const;

export type SpinnerProps = Omit<ComponentPropsWithoutRef<'svg'>, 'children'> & {
  /** Preset size. Defaults to 'md'. Can be overridden via className. */
  size?: keyof typeof sizeClasses;
};

export function Spinner({ className, size = 'md', ...props }: SpinnerProps) {
  const mergedClassName = ['animate-spin text-pf-accent', sizeClasses[size], className].filter(Boolean).join(' ');

  return (
    <svg
      {...props}
      className={mergedClassName}
      xmlns="http://www.w3.org/2000/svg"
      fill="none"
      viewBox="0 0 24 24"
      aria-hidden={props['aria-label'] ? undefined : true}
    >
      <circle
        className="opacity-25"
        cx="12"
        cy="12"
        r="10"
        stroke="currentColor"
        strokeWidth="4"
      />
      <path
        className="opacity-75"
        fill="currentColor"
        d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
      />
    </svg>
  );
}
