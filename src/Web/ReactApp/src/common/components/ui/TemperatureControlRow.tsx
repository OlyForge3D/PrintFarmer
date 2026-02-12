import React from 'react';
import { TemperatureInput } from './TemperatureInput';

export interface TemperatureControlRowProps {
  /** Icon element (e.g., NozzleIcon, BedIcon) */
  icon: React.ReactNode;
  /** Label text (e.g., "Hotend", "Bed") */
  label: string;
  /** Live temperature reading string (e.g., "25.6°C → 210°C") */
  liveReading: string;
  /** Target temperature value in the input */
  value: number | string;
  /** Callback when input value changes */
  onChange: (e: React.ChangeEvent<HTMLInputElement>) => void;
  /** Callback for key down on input (e.g., Enter to apply) */
  onKeyDown?: (e: React.KeyboardEvent<HTMLInputElement>) => void;
  /** Whether the input is disabled */
  disabled?: boolean;
}

/**
 * Composite temperature control row for the sidebar.
 * Layout: [icon] [label] [live reading] [temp input °C]
 */
export function TemperatureControlRow({
  icon,
  label,
  liveReading,
  value,
  onChange,
  onKeyDown,
  disabled = false,
}: TemperatureControlRowProps) {
  return (
    <div className="flex items-center gap-2 py-1">
      <div className="shrink-0">{icon}</div>
      <span className="text-xs text-pf-text-secondary shrink-0">{label}</span>
      <span className="text-xs text-slate-400 text-right flex-1 tabular-nums">{liveReading}</span>
      <TemperatureInput
        value={value}
        onChange={onChange}
        onKeyDown={onKeyDown}
        disabled={disabled}
        className="w-16"
      />
    </div>
  );
}
