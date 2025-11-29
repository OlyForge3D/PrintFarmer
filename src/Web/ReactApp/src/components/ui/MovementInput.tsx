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
}

/**
 * Movement/distance input component for XYZ axis controls
 * Used for printer head movement controls (nozzle positioning)
 * Features: Number input with axis label, compact sizing, spinner button removal
 */
export function MovementInput({
  axis,
  value,
  onChange,
  step = 1,
  disabled = false,
  className = '',
  ...props
}: MovementInputProps) {
  const deltaSymbol = '∆';

  return (
    <div className="relative inline-block">
      <span className="absolute left-2 text-slate-500 text-xs pointer-events-none z-10 top-1/2 transform -translate-y-1/2">
        {axis}
      </span>
      <Input
        type="number"
        step={step}
        value={value}
        onChange={onChange}
        disabled={disabled}
        placeholder={`${deltaSymbol}${axis}`}
        aria-label={`${axis} movement amount`}
        className={`w-24 h-8 pl-6 pr-2 text-xs [&::-webkit-outer-spin-button]:appearance-none [&::-webkit-inner-spin-button]:appearance-none [&]:m-0 ${className}`}
        {...props}
      />
    </div>
  );
}
