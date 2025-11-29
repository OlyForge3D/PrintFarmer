import React from 'react';
import { Input, InputProps } from './Input';

export interface TemperatureInputProps extends Omit<InputProps, 'type'> {
  /** Label for the input (e.g., "Hotend", "Bed") */
  label?: string;
  /** Current temperature value */
  value: number | string;
  /** Callback when value changes */
  onChange: (e: React.ChangeEvent<HTMLInputElement>) => void;
  /** Step value for increment/decrement */
  step?: number;
}

/**
 * Temperature input component with °C unit label
 * Used for hotend and bed temperature controls
 * Features: Number input with °C suffix, spinner button removal, and special sizing
 */
export function TemperatureInput({
  label,
  value,
  onChange,
  step = 0.1,
  disabled = false,
  className = '',
  ...props
}: TemperatureInputProps) {
  return (
    <div className="flex items-center">
      {label && (
        <span className="absolute left-2 text-slate-500 text-xs pointer-events-none z-10 top-1/2 transform -translate-y-1/2">
          {label}
        </span>
      )}
      <div className="relative inline-block">
        <Input
          type="number"
          step={step}
          value={value}
          onChange={onChange}
          disabled={disabled}
          placeholder="Temp"
          className={`w-28 h-9 ${label ? 'pl-10' : 'pl-2'} pr-8 ${className}`}
          {...props}
        />
        <span className="absolute right-2 top-1/2 transform -translate-y-1/2 text-slate-500 pointer-events-none text-sm">
          °C
        </span>
      </div>
    </div>
  );
}
