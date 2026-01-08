/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import clsx from 'clsx';

export interface CheckboxProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type'> {
  /** Label text to display next to the checkbox */
  label?: string;
  /** Whether the checkbox is in an invalid state */
  invalid?: boolean;
}

export const Checkbox: React.FC<CheckboxProps> = ({
  label,
  invalid,
  className,
  id,
  ...rest
}) => {
  const inputId = id || (label ? `checkbox-${label.replace(/\s+/g, '-').toLowerCase()}` : undefined);
  
  const checkbox = (
    <input
      type="checkbox"
      id={inputId}
      className={clsx(
        'w-4 h-4 rounded border-pf-border bg-pf-bg-0 text-pf-accent',
        'focus:outline-none focus:ring-2 focus:ring-pf-accent focus:ring-offset-1 focus:ring-offset-pf-bg-0',
        'checked:bg-pf-accent checked:border-pf-accent',
        'disabled:opacity-50 disabled:cursor-not-allowed',
        'transition-colors cursor-pointer',
        invalid && 'border-pf-error focus:ring-pf-error',
        className
      )}
      {...rest}
    />
  );

  if (label) {
    return (
      <label className="inline-flex items-center gap-2 cursor-pointer select-none">
        {checkbox}
        <span className={clsx(
          'text-sm text-pf-text-primary',
          rest.disabled && 'opacity-50 cursor-not-allowed'
        )}>
          {label}
        </span>
      </label>
    );
  }

  return checkbox;
};

export default Checkbox;
