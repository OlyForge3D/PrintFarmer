import React from 'react';
import { Input, InputProps } from './Input';

export interface MovementInputProps extends Omit<InputProps, 'type'> {
  /** Axis label: X, Y, or Z */
  axis: 'X' | 'Y' | 'Z';
  /** Current movement value */
  value: number | string;
  /** Callback when value changes */
  onChange: (e: React.ChangeEvent<HTMLInputElement>) => void;
  /** Step value for increment/decrement */
  step?: number;
  /** Current position to display as a groupbox-style label overlapping the top border */
  currentPosition?: number | null;
}

/**
 * Movement/distance input component for XYZ axis controls
 * Used for printer head movement controls (nozzle positioning)
 * Features: Number input with axis label, compact sizing, spinner button removal
 * When `currentPosition` is provided, renders a groupbox-style label showing
 * the current axis position overlapping the top-right border of the input.
 */
export function MovementInput({
  axis,
  value,
  onChange,
  step = 1,
  disabled = false,
  className,
  currentPosition,
  ...props
}: MovementInputProps) {
  const positionLabel = currentPosition != null ? `[ ${(currentPosition ?? 0).toFixed(1)} ]` : '[ --- ]';

  return (
    <div className="relative pt-2 inline-block">
      <span className="absolute -top-0.5 right-2 px-1 bg-pf-bg-1 text-[10px] font-bold text-pf-text-secondary z-10">
        {positionLabel}
      </span>
      <span className="absolute left-2 bottom-1.5 text-pf-text-secondary text-xs pointer-events-none z-10">
        {axis}
      </span>
      <Input
        type="number"
        step={step}
        value={value}
        onChange={onChange}
        disabled={disabled}
        aria-label={`${axis} movement amount`}
        className={`w-24 h-8 pl-6 pr-2 text-xs text-right [&::-webkit-outer-spin-button]:appearance-none [&::-webkit-inner-spin-button]:appearance-none [&]:m-0 ${className ?? ''}`}
        {...props}
      />
    </div>
  );
}
