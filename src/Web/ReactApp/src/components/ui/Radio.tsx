import React from 'react';
import clsx from 'clsx';

export interface RadioProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type'> {
  /** Label text to display next to the radio button */
  label?: string;
  /** Whether the radio is in an invalid state */
  invalid?: boolean;
}

export const Radio: React.FC<RadioProps> = ({
  label,
  invalid,
  className,
  id,
  ...rest
}) => {
  const inputId = id || (label ? `radio-${label.replace(/\s+/g, '-').toLowerCase()}` : undefined);
  
  const radio = (
    <input
      type="radio"
      id={inputId}
      className={clsx(
        'w-4 h-4 border-pf-border bg-pf-bg-0 text-pf-accent',
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
        {radio}
        <span className={clsx(
          'text-sm text-pf-text-primary',
          rest.disabled && 'opacity-50 cursor-not-allowed'
        )}>
          {label}
        </span>
      </label>
    );
  }

  return radio;
};

export default Radio;
