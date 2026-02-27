import React, { useId } from 'react';
import clsx from 'clsx';
import { Button } from './Button';

interface NumberStepperProps {
  value: number;
  onChange: (value: number) => void;
  min?: number;
  max?: number;
  step?: number;
  id?: string;
  'aria-label'?: string;
  className?: string;
}

/**
 * A compact stepper with − / + buttons flanking a number input.
 * Mirrors the Spoolman clone-quantity control.
 */
export const NumberStepper: React.FC<NumberStepperProps> = ({
  value,
  onChange,
  min = 0,
  max = 100,
  step = 1,
  id,
  'aria-label': ariaLabel,
  className,
}) => {
  const fallbackId = useId();
  const inputId = id ?? fallbackId;

  const clamp = (v: number) => Math.max(min, Math.min(max, v));

  return (
    <div className={clsx('inline-flex items-center border border-pf-border rounded-sm overflow-hidden', className)}>
      <Button
        variant="unstyled"
        type="button"
        className="px-2.5 py-1.5 text-sm font-medium text-pf-text-primary bg-pf-bg-1 hover:bg-pf-bg-2 transition disabled:opacity-40 disabled:cursor-not-allowed"
        onClick={() => onChange(clamp(value - step))}
        disabled={value <= min}
        aria-label="Decrease"
      >
        −
      </Button>
      <input
        id={inputId}
        type="number"
        className="w-12 text-center text-sm bg-pf-bg-0 text-pf-text-primary border-x border-pf-border py-1.5 focus:outline-hidden focus:ring-1 focus:ring-pf-accent [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
        value={value}
        onChange={e => onChange(clamp(parseInt(e.target.value) || min))}
        min={min}
        max={max}
        step={step}
        aria-label={ariaLabel}
      />
      <Button
        variant="unstyled"
        type="button"
        className="px-2.5 py-1.5 text-sm font-medium text-pf-text-primary bg-pf-bg-1 hover:bg-pf-bg-2 transition disabled:opacity-40 disabled:cursor-not-allowed"
        onClick={() => onChange(clamp(value + step))}
        disabled={value >= max}
        aria-label="Increase"
      >
        +
      </Button>
    </div>
  );
};

export default NumberStepper;
