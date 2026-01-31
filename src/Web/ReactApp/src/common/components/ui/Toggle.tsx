/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import clsx from 'clsx';

export interface ToggleProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type' | 'size'> {
  /** Label text to display next to the toggle */
  label?: string;
  /** Size of the toggle */
  size?: 'sm' | 'md';
  /** Whether the toggle is in an invalid state */
  invalid?: boolean;
}

export const Toggle: React.FC<ToggleProps> = ({
  label,
  size = 'md',
  invalid,
  className,
  id,
  checked,
  disabled,
  ...rest
}) => {
  const inputId = id || (label ? `toggle-${label.replace(/\s+/g, '-').toLowerCase()}` : undefined);
  
  const sizeClasses = {
    sm: {
      track: 'w-8 h-4',
      thumb: 'w-3 h-3',
      translate: 'translate-x-4',
    },
    md: {
      track: 'w-11 h-6',
      thumb: 'w-5 h-5',
      translate: 'translate-x-5',
    },
  };

  const toggle = (
    <label className={clsx(
      'relative inline-flex items-center cursor-pointer',
      disabled && 'cursor-not-allowed opacity-50'
    )}>
      <input
        type="checkbox"
        id={inputId}
        checked={checked}
        disabled={disabled}
        className="sr-only peer"
        {...rest}
      />
      <div
        className={clsx(
          'rounded-full transition-colors',
          sizeClasses[size].track,
          'bg-pf-bg-2 peer-checked:bg-pf-accent',
          'peer-focus:outline-hidden peer-focus:ring-2 peer-focus:ring-pf-accent peer-focus:ring-offset-1 peer-focus:ring-offset-pf-bg-0',
          invalid && 'ring-1 ring-pf-error',
          className
        )}
      >
        <div
          className={clsx(
            'absolute top-0.5 left-0.5 rounded-full bg-white shadow-sm transition-transform',
            sizeClasses[size].thumb,
            checked && sizeClasses[size].translate
          )}
        />
      </div>
    </label>
  );

  if (label) {
    return (
      <div className="inline-flex items-center gap-2">
        {toggle}
        <span className={clsx(
          'text-sm text-pf-text-primary select-none',
          disabled && 'opacity-50'
        )}>
          {label}
        </span>
      </div>
    );
  }

  return toggle;
};

export default Toggle;
