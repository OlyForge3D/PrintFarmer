import React from 'react';
import { TemperatureInput } from './TemperatureInput';
import { Select } from './Select';

export interface TemperaturePresetOption {
  label: string;
  value: string;
}

export interface TemperatureControlRowProps {
  /** Icon element (e.g., NozzleIcon, BedIcon) */
  icon: React.ReactNode;
  /** Label text (e.g., "Hotend", "Bed") */
  label: string;
  /** Heater state text (e.g., "on", "off") */
  stateLabel?: string;
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
  /** Optional preset options for this specific heater row */
  presetOptions?: TemperaturePresetOption[];
  /** Callback when a row-specific preset is selected */
  onPresetSelect?: (preset: string) => void;
}

/**
 * Composite temperature control row for the sidebar.
 * Layout: [icon] [label] [live reading] [temp input °C]
 */
export function TemperatureControlRow({
  icon,
  label,
  stateLabel,
  liveReading,
  value,
  onChange,
  onKeyDown,
  disabled = false,
  presetOptions,
  onPresetSelect,
}: TemperatureControlRowProps) {
  const hasRowPresetSelector = Boolean(presetOptions && presetOptions.length > 0 && onPresetSelect);

  return (
    <div className="grid grid-cols-[minmax(0,1fr)_3rem_4.75rem_5rem_1.5rem] items-center gap-2 py-1">
      <div className="flex items-center gap-2 min-w-0">
        <div className="shrink-0">{icon}</div>
        <span className="text-xs text-pf-text-secondary truncate">{label}</span>
      </div>
      <span className="text-xs text-slate-400 text-right tabular-nums">{stateLabel ?? '—'}</span>
      <span className="text-xs text-slate-400 text-right tabular-nums">{liveReading}</span>
      <TemperatureInput
        value={value}
        onChange={onChange}
        onKeyDown={onKeyDown}
        disabled={disabled}
        className="w-full"
      />
      {hasRowPresetSelector ? (
        <div className="h-9 w-6">
          <Select
            value=""
            aria-label={`Apply ${label} preset`}
            disabled={disabled}
            onChange={(e) => {
              const preset = e.target.value;
              if (preset) {
                onPresetSelect?.(preset);
              }
            }}
            className="h-9 !p-0 !pr-0 !border-transparent !bg-transparent text-transparent focus:ring-0 focus:border-transparent"
            containerClassName="h-9 w-6"
          >
            <option value="" disabled hidden></option>
            {presetOptions?.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>
        </div>
      ) : (
        <div className="h-9 w-5" />
      )}
    </div>
  );
}
