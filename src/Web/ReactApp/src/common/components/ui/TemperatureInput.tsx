import React, { useId } from 'react';
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
  /** Callback for key down events */
  onKeyDown?: (e: React.KeyboardEvent<HTMLInputElement>) => void;
  /** Current temperature reading to display as a groupbox-style label overlapping the top border */
  currentTemp?: string | null;
}

/**
 * Temperature input component with °C unit label
 * Used for hotend and bed temperature controls
 * Features: Number input with °C suffix, spinner button removal, and special sizing
 * When `currentTemp` is provided, renders a groupbox-style label showing
 * the current reading overlapping the top-right border of the input.
 */
export function TemperatureInput({
  label,
  value,
  onChange,
  onKeyDown,
  step = 0.1,
  disabled = false,
  className,
  currentTemp,
  ...props
}: TemperatureInputProps) {
  const inputId = useId();
  return (
    <div className="flex items-center">
      {label && (
        <label htmlFor={inputId} className="absolute left-2 text-pf-text-secondary text-xs pointer-events-none z-10 top-1/2 transform -translate-y-1/2">
          {label}
        </label>
      )}
      <div className={`relative inline-block w-20 ${currentTemp != null ? 'pt-2' : ''} ${className ?? ''}`}>
        {currentTemp != null && (
          <span className="absolute -top-0.5 right-2 px-1 bg-pf-bg-1 text-[10px] font-bold text-pf-text-secondary z-10">
            [ {currentTemp} ]
          </span>
        )}
        <Input
          id={inputId}
          type="number"
          step={step}
          value={value}
          onChange={onChange}
          onKeyDown={onKeyDown}
          disabled={disabled}
          aria-label={label}
          placeholder="Temp"
          className={`h-9 ${label ? 'pl-10' : 'pl-2'} pr-8 text-xs [&::-webkit-outer-spin-button]:appearance-none [&::-webkit-inner-spin-button]:appearance-none [&]:m-0`}
          {...props}
        />
        <span className="absolute right-2 bottom-1.5 text-pf-text-secondary pointer-events-none text-sm">
          °C
        </span>
      </div>
    </div>
  );
}
