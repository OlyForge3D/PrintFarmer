import React from 'react';
import clsx from 'clsx';
import { Radio } from './Radio';

export interface RadioOption {
  value: string;
  label: string;
  disabled?: boolean;
}

export interface RadioGroupProps {
  /** The name attribute for all radio inputs in the group */
  name: string;
  /** Array of options to display */
  options: RadioOption[];
  /** Currently selected value */
  value?: string;
  /** Callback when selection changes */
  onChange?: (value: string) => void;
  /** Whether the entire group is disabled */
  disabled?: boolean;
  /** Whether the group is in an invalid state */
  invalid?: boolean;
  /** Layout direction */
  direction?: 'horizontal' | 'vertical';
  /** Additional className for the container */
  className?: string;
  /** Accessible name (via id reference). Required for screen-reader affordance on the radiogroup itself. */
  'aria-labelledby'?: string;
  /** Optional accessible description (via id reference). */
  'aria-describedby'?: string;
  /** Optional accessible name inline (use aria-labelledby when a visible label exists). */
  'aria-label'?: string;
}

export const RadioGroup: React.FC<RadioGroupProps> = ({
  name,
  options,
  value,
  onChange,
  disabled,
  invalid,
  direction = 'vertical',
  className,
  'aria-labelledby': ariaLabelledBy,
  'aria-describedby': ariaDescribedBy,
  'aria-label': ariaLabel,
}) => {
  return (
    <div
      className={clsx(
        'flex gap-3',
        direction === 'vertical' ? 'flex-col' : 'flex-row flex-wrap',
        className
      )}
      role="radiogroup"
      aria-labelledby={ariaLabelledBy}
      aria-describedby={ariaDescribedBy}
      aria-label={ariaLabel}
    >
      {options.map((option) => (
        <Radio
          key={option.value}
          name={name}
          value={option.value}
          label={option.label}
          checked={value === option.value}
          disabled={disabled || option.disabled}
          invalid={invalid}
          onChange={() => onChange?.(option.value)}
        />
      ))}
    </div>
  );
};

export default RadioGroup;
