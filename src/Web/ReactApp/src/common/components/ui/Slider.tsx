import React from 'react';
import clsx from 'clsx';

export interface SliderProps {
  /** Current value */
  value: number;
  /** Callback when value changes */
  onChange: (value: number) => void;
  /** Minimum value */
  min?: number;
  /** Maximum value */
  max?: number;
  /** Step increment */
  step?: number;
  /** Whether the slider is disabled */
  disabled?: boolean;
  /** Optional CSS class name */
  className?: string;
  /** Accessible label */
  'aria-label'?: string;
  /** ID of element that labels this slider */
  'aria-labelledby'?: string;
}

/**
 * Slider component for selecting numeric values within a range.
 * Built with accessibility in mind using native range input.
 */
export const Slider: React.FC<SliderProps> = ({
  value,
  onChange,
  min = 0,
  max = 100,
  step = 1,
  disabled = false,
  className,
  'aria-label': ariaLabel,
  'aria-labelledby': ariaLabelledby,
}) => {
  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    onChange(parseFloat(e.target.value));
  };

  // Calculate percentage for custom track fill
  const percentage = ((value - min) / (max - min)) * 100;

  return (
    <input
      type="range"
      value={value}
      onChange={handleChange}
      min={min}
      max={max}
      step={step}
      disabled={disabled}
      aria-label={ariaLabel}
      aria-labelledby={ariaLabelledby}
      className={clsx(
        'w-full h-2 rounded-lg appearance-none cursor-pointer',
        'bg-pf-bg-1',
        'focus:outline-hidden focus:ring-2 focus:ring-pf-accent focus:ring-offset-2 focus:ring-offset-pf-bg-0',
        'disabled:opacity-50 disabled:cursor-not-allowed',
        // Custom thumb styling
        '[&::-webkit-slider-thumb]:appearance-none',
        '[&::-webkit-slider-thumb]:w-4',
        '[&::-webkit-slider-thumb]:h-4',
        '[&::-webkit-slider-thumb]:rounded-full',
        '[&::-webkit-slider-thumb]:bg-pf-accent-bg',
        '[&::-webkit-slider-thumb]:cursor-pointer',
        '[&::-webkit-slider-thumb]:transition-transform',
        '[&::-webkit-slider-thumb]:hover:scale-110',
        '[&::-webkit-slider-thumb]:active:scale-95',
        // Firefox thumb
        '[&::-moz-range-thumb]:w-4',
        '[&::-moz-range-thumb]:h-4',
        '[&::-moz-range-thumb]:rounded-full',
        '[&::-moz-range-thumb]:bg-pf-accent-bg',
        '[&::-moz-range-thumb]:border-0',
        '[&::-moz-range-thumb]:cursor-pointer',
        // Track styling
        '[&::-webkit-slider-runnable-track]:rounded-lg',
        '[&::-moz-range-track]:rounded-lg',
        className
      )}
      style={{
        background: `linear-gradient(to right, var(--color-pf-primary) 0%, var(--color-pf-primary) ${percentage}%, var(--color-pf-surface) ${percentage}%, var(--color-pf-surface) 100%)`,
      }}
    />
  );
};

export default Slider;
