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
}) => {
  return (
    <div
      className={clsx(
        'flex gap-3',
        direction === 'vertical' ? 'flex-col' : 'flex-row flex-wrap',
        className
      )}
      role="radiogroup"
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
