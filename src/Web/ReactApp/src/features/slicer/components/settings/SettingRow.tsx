/**
 * Individual setting row component with OrcaSlicer-style UI
 * Supports slider, select, radio, and checkbox control types
 * Includes change tracking with reset-to-original functionality
 */
import React, { useId } from 'react';
import { Button, Checkbox } from '@/common/components/ui';
import { HelpIcon } from './SlicerSettingIcons';

/** Reset icon - circular arrow matching OrcaSlicer's style */
const ResetIcon: React.FC<{ className?: string }> = ({ className = 'w-4 h-4' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8" />
    <path d="M3 3v5h5" />
  </svg>
);

interface BaseSettingRowProps {
  icon?: React.ReactNode;
  label: string;
  description?: string;
  tooltip?: string;
  disabled?: boolean;
  /** Whether this setting has been modified from its original value */
  isModified?: boolean;
  /** Callback to reset this setting to its original value */
  onReset?: () => void;
  /** The original value (displayed in reset tooltip) */
  originalValue?: unknown;
}

interface SliderSettingProps extends BaseSettingRowProps {
  type: 'slider';
  value: number;
  onChange: (value: number) => void;
  min: number;
  max: number;
  step?: number;
  unit?: string;
  showTicks?: boolean;
  tickLabels?: string[];
}

interface SelectSettingProps extends BaseSettingRowProps {
  type: 'select';
  value: string;
  onChange: (value: string) => void;
  options: Array<{ value: string; label: string; icon?: React.ReactNode }>;
}

interface RadioSettingProps extends BaseSettingRowProps {
  type: 'radio';
  value: string;
  onChange: (value: string) => void;
  options: Array<{ value: string; label: string }>;
}

interface CheckboxSettingProps extends BaseSettingRowProps {
  type: 'checkbox';
  checked: boolean;
  onChange: (checked: boolean) => void;
}

interface NumberInputSettingProps extends BaseSettingRowProps {
  type: 'number';
  value: number;
  onChange: (value: number) => void;
  min?: number;
  max?: number;
  step?: number;
  unit?: string;
}

interface TextInputSettingProps extends BaseSettingRowProps {
  type: 'text';
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
}

interface ColorInputSettingProps extends BaseSettingRowProps {
  type: 'color';
  value: string;
  onChange: (value: string) => void;
}

export type SettingRowProps =
  | SliderSettingProps
  | SelectSettingProps
  | RadioSettingProps
  | CheckboxSettingProps
  | NumberInputSettingProps
  | TextInputSettingProps
  | ColorInputSettingProps;

/**
 * SettingRow - OrcaSlicer-style setting control with icon, label, description, and control
 * Supports change tracking with reset-to-original functionality
 */
export const SettingRow: React.FC<SettingRowProps> = (props) => {
  const id = useId();
  const { icon, label, description, tooltip, disabled, isModified, onReset, originalValue } = props;

  // Format original value for tooltip display
  const formatOriginalValue = (value: unknown): string => {
    if (value === undefined || value === null) return 'N/A';
    if (typeof value === 'boolean') return value ? 'Enabled' : 'Disabled';
    if (typeof value === 'number') return String(value);
    if (typeof value === 'string') return value;
    return JSON.stringify(value);
  };

  const renderControl = () => {
    switch (props.type) {
      case 'slider':
        return <SliderControl {...props} id={id} />;
      case 'select':
        return <SelectControl {...props} id={id} />;
      case 'radio':
        return <RadioControl {...props} id={id} />;
      case 'checkbox':
        return <CheckboxControl {...props} id={id} />;
      case 'number':
        return <NumberInputControl {...props} id={id} />;
      case 'text':
        return <TextInputControl {...props} id={id} />;
      case 'color':
        return <ColorInputControl {...props} id={id} />;
      default:
        return null;
    }
  };

  return (
    <div className={`py-2 ${disabled ? 'opacity-50' : ''}`}>
      {/* Header row with icon, label, reset button, and help */}
      <div className="flex items-center gap-2 mb-1">
        <span className="text-pf-accent-2">{icon}</span>
        
        {/* Reset button - shown when setting is modified */}
        {isModified && onReset && (
          <Button
            variant="subtle"
            type="button"
            onClick={onReset}
            className="p-0.5 text-pf-warning hover:text-pf-warning transition-colors
                       hover:bg-pf-warning/10 rounded"
            title={`Reset to original: ${formatOriginalValue(originalValue)}`}
            aria-label={`Reset ${label} to original value`}
          >
            <ResetIcon className="w-4 h-4" />
          </Button>
        )}
        
        {/* Label - highlighted in accent color when modified */}
        <label 
          htmlFor={id} 
          className={`font-semibold transition-colors ${
            isModified 
              ? 'text-pf-warning' 
              : 'text-pf-text'
          }`}
        >
          {label}
        </label>
        
        {tooltip && (
          <Button
            variant="subtle"
            type="button"
            className="p-0.5"
            title={tooltip}
            aria-label={`Help for ${label}`}
          >
            <HelpIcon className="w-4 h-4" />
          </Button>
        )}
      </div>
      
      {/* Description */}
      {description && (
        <p className="text-xs text-pf-text-muted mb-1.5">{description}</p>
      )}
      
      {/* Control */}
      {renderControl()}
    </div>
  );
};

/** Slider control with tick marks */
const SliderControl: React.FC<SliderSettingProps & { id: string }> = ({
  id,
  value,
  onChange,
  min,
  max,
  step = 1,
  unit = '',
  showTicks = true,
  tickLabels,
  disabled,
}) => {
  const percentage = ((value - min) / (max - min)) * 100;
  
  // Generate tick positions
  const ticks = tickLabels || [];
  const numTicks = ticks.length || 5;
  const tickPositions = Array.from({ length: numTicks }, (_, i) => 
    min + (i * (max - min)) / (numTicks - 1)
  );

  return (
    <div className="relative">
      {/* Slider track with custom styling */}
      <div className="relative h-6 flex items-center">
        <input
          id={id}
          type="range"
          min={min}
          max={max}
          step={step}
          value={value}
          onChange={(e) => onChange(Number(e.target.value))}
          disabled={disabled}
          className="w-full h-2 bg-pf-border rounded-full appearance-none cursor-pointer
                     [&::-webkit-slider-thumb]:appearance-none
                     [&::-webkit-slider-thumb]:w-5
                     [&::-webkit-slider-thumb]:h-5
                     [&::-webkit-slider-thumb]:rounded-full
                     [&::-webkit-slider-thumb]:bg-pf-accent-2
                     [&::-webkit-slider-thumb]:border-2
                     [&::-webkit-slider-thumb]:border-pf-bg-0
                     [&::-webkit-slider-thumb]:shadow-md
                     [&::-webkit-slider-thumb]:cursor-pointer
                     [&::-moz-range-thumb]:w-5
                     [&::-moz-range-thumb]:h-5
                     [&::-moz-range-thumb]:rounded-full
                     [&::-moz-range-thumb]:bg-pf-accent-2
                     [&::-moz-range-thumb]:border-2
                     [&::-moz-range-thumb]:border-pf-bg-0
                     [&::-moz-range-thumb]:cursor-pointer
                     disabled:opacity-50 disabled:cursor-not-allowed"
          style={{
            background: `linear-gradient(to right, var(--pf-accent-2) 0%, var(--pf-accent-2) ${percentage}%, var(--pf-border) ${percentage}%, var(--pf-border) 100%)`
          }}
        />
        
        {/* Current value indicator below thumb */}
        <div 
          className="absolute top-5 transform -translate-x-1/2 text-xs font-bold text-pf-text"
          style={{ left: `${percentage}%` }}
        >
          {value}{unit}
        </div>
      </div>
      
      {/* Tick marks and labels */}
      {showTicks && (
        <div className="flex justify-between mt-3 px-1">
          {tickPositions.map((tickValue, i) => (
            <div key={i} className="flex flex-col items-center">
              <div className="w-px h-2 bg-pf-border-light" />
              <span className="text-xs text-pf-text-muted mt-1">
                {tickLabels?.[i] ?? `${Math.round(tickValue)}${unit}`}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};

/** Dropdown select control */
const SelectControl: React.FC<SelectSettingProps & { id: string }> = ({
  id,
  value,
  onChange,
  options,
  disabled,
}) => {
  const selectedOption = options.find(o => o.value === value);
  
  return (
    <div className="relative">
      {/* eslint-disable-next-line local/pf-no-raw-html-controls -- Custom OrcaSlicer-style dropdown with icon preview */}
      <select
        id={id}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        className="w-full px-4 py-3 bg-pf-panel border border-pf-border rounded-lg
                   text-pf-text appearance-none bg-none cursor-pointer
                   hover:border-pf-border-light focus:border-pf-accent-2 focus:outline-hidden
                   disabled:opacity-50 disabled:cursor-not-allowed"
      >
        {options.map((opt) => (
          <option key={opt.value} value={opt.value}>
            {opt.label}
          </option>
        ))}
      </select>
      
      {/* Custom dropdown arrow */}
      <div className="absolute right-4 top-1/2 -translate-y-1/2 pointer-events-none">
        <svg className="w-5 h-5 text-pf-text-muted" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M5.293 7.293a1 1 0 011.414 0L10 10.586l3.293-3.293a1 1 0 111.414 1.414l-4 4a1 1 0 01-1.414 0l-4-4a1 1 0 010-1.414z" clipRule="evenodd" />
        </svg>
      </div>
      
      {/* Selected option icon preview */}
      {selectedOption?.icon && (
        <div className="absolute left-4 top-1/2 -translate-y-1/2 pointer-events-none text-pf-accent-2">
          {selectedOption.icon}
        </div>
      )}
    </div>
  );
};

/** Radio button group control */
const RadioControl: React.FC<RadioSettingProps & { id: string }> = ({
  id,
  value,
  onChange,
  options,
  disabled,
}) => {
  return (
    <div className="space-y-2">
      {options.map((opt) => (
        <label
          key={opt.value}
          className={`flex items-center gap-3 cursor-pointer ${disabled ? 'cursor-not-allowed' : ''}`}
        >
          <div className="relative">
            {/* eslint-disable-next-line local/pf-no-raw-html-controls -- Custom OrcaSlicer-style radio with animated indicator */}
            <input
              type="radio"
              name={id}
              value={opt.value}
              checked={value === opt.value}
              onChange={(e) => onChange(e.target.value)}
              disabled={disabled}
              className="sr-only"
            />
            <div className={`w-5 h-5 rounded-full border-2 transition-colors
                            ${value === opt.value 
                              ? 'border-pf-accent-2 bg-pf-accent-2' 
                              : 'border-pf-border bg-transparent hover:border-pf-border-light'}`}
            >
              {value === opt.value && (
                <div className="absolute inset-0 flex items-center justify-center">
                  <div className="w-2 h-2 rounded-full bg-pf-bg-0" />
                </div>
              )}
            </div>
          </div>
          <span className="text-pf-text">{opt.label}</span>
        </label>
      ))}
    </div>
  );
};

/** Checkbox control */
const CheckboxControl: React.FC<CheckboxSettingProps & { id: string }> = ({
  id,
  checked,
  onChange,
  disabled,
}) => {
  return (
    <label className={`flex items-center gap-3 cursor-pointer ${disabled ? 'cursor-not-allowed' : ''}`}>
      <div className="relative">
        {/* eslint-disable-next-line local/pf-no-raw-html-controls -- Custom OrcaSlicer-style checkbox with SVG checkmark */}
        <input
          id={id}
          type="checkbox"
          checked={checked}
          onChange={(e) => onChange(e.target.checked)}
          disabled={disabled}
          className="sr-only"
        />
        <div className={`w-5 h-5 rounded border-2 transition-colors
                        ${checked 
                          ? 'border-pf-accent-2 bg-pf-accent-2' 
                          : 'border-pf-border bg-transparent hover:border-pf-border-light'}`}
        >
          {checked && (
            <svg className="w-full h-full text-pf-bg-0" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
            </svg>
          )}
        </div>
      </div>
    </label>
  );
};

/** Number input with unit */
const NumberInputControl: React.FC<NumberInputSettingProps & { id: string }> = ({
  id,
  value,
  onChange,
  min,
  max,
  step = 0.01,
  unit,
  disabled,
}) => {
  return (
    <div className="flex items-center gap-2">
      <input
        id={id}
        type="number"
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        min={min}
        max={max}
        step={step}
        disabled={disabled}
        className="flex-1 px-4 py-2 bg-pf-panel border border-pf-border rounded-lg
                   text-pf-text text-right
                   hover:border-pf-border-light focus:border-pf-accent-2 focus:outline-hidden
                   disabled:opacity-50 disabled:cursor-not-allowed"
      />
      {unit && (
        <span className="text-sm text-pf-text-muted px-2 py-2 bg-pf-border rounded-sm">
          {unit}
        </span>
      )}
    </div>
  );
};

/** Text input control */
const TextInputControl: React.FC<TextInputSettingProps & { id: string }> = ({
  id,
  value,
  onChange,
  placeholder,
  disabled,
}) => {
  return (
    <input
      id={id}
      type="text"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={placeholder}
      disabled={disabled}
      className="w-full px-4 py-2 bg-pf-panel border border-pf-border rounded-lg
                 text-pf-text
                 hover:border-pf-border-light focus:border-pf-accent-2 focus:outline-hidden
                 disabled:opacity-50 disabled:cursor-not-allowed"
    />
  );
};

/** Color input control with preview swatch */
const ColorInputControl: React.FC<ColorInputSettingProps & { id: string }> = ({
  id,
  value,
  onChange,
  disabled,
}) => {
  return (
    <div className="flex items-center gap-3">
      <input
        id={id}
        type="color"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        className="w-12 h-10 rounded cursor-pointer border border-pf-border
                   disabled:opacity-50 disabled:cursor-not-allowed"
      />
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        className="flex-1 px-3 py-2 bg-pf-panel border border-pf-border rounded-lg
                   text-pf-text font-mono text-sm uppercase
                   hover:border-pf-border-light focus:border-pf-accent-2 focus:outline-hidden
                   disabled:opacity-50 disabled:cursor-not-allowed"
        placeholder="#000000"
      />
    </div>
  );
};

// ============================================================================
// COMPACT SETTING ROW - OrcaSlicer-style inline layout
// ============================================================================

interface CompactSettingRowBaseProps {
  label: string;
  tooltip?: string;
  disabled?: boolean;
  /** Whether this setting has been modified from its original value */
  isModified?: boolean;
  /** Callback to reset this setting to its original value */
  onReset?: () => void;
  /** The original value (displayed in reset tooltip) */
  originalValue?: unknown;
}

interface CompactNumberSettingProps extends CompactSettingRowBaseProps {
  type: 'number';
  value: number;
  onChange: (value: number) => void;
  min?: number;
  max?: number;
  step?: number;
  unit?: string;
}

interface CompactSelectSettingProps extends CompactSettingRowBaseProps {
  type: 'select';
  value: string;
  onChange: (value: string) => void;
  options: Array<{ value: string; label: string }>;
}

interface CompactCheckboxSettingProps extends CompactSettingRowBaseProps {
  type: 'checkbox';
  checked: boolean;
  onChange: (checked: boolean) => void;
}

interface CompactTextSettingProps extends CompactSettingRowBaseProps {
  type: 'text';
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
}

export type CompactSettingRowProps =
  | CompactNumberSettingProps
  | CompactSelectSettingProps
  | CompactCheckboxSettingProps
  | CompactTextSettingProps;

/**
 * CompactSettingRow - OrcaSlicer-style compact setting row
 * Label on left, small input on right, with optional unit indicator
 */
export const CompactSettingRow: React.FC<CompactSettingRowProps> = (props) => {
  const id = React.useId();
  const { label, tooltip, disabled, isModified, onReset, originalValue } = props;

  const formatOriginalValue = (value: unknown): string => {
    if (value === undefined || value === null) return 'N/A';
    if (typeof value === 'boolean') return value ? 'Enabled' : 'Disabled';
    if (typeof value === 'number') return String(value);
    if (typeof value === 'string') return value;
    return JSON.stringify(value);
  };

  const renderControl = () => {
    switch (props.type) {
      case 'number':
        return (
          <div className="flex items-center gap-1">
            <input
              id={id}
              type="number"
              value={props.value}
              onChange={(e) => props.onChange(Number(e.target.value))}
              min={props.min}
              max={props.max}
              step={props.step ?? 0.01}
              disabled={disabled}
              className="w-20 px-2 py-1 text-sm text-right bg-pf-panel border border-pf-border rounded
                         text-pf-text focus:border-pf-accent-2 focus:outline-hidden
                         disabled:opacity-50 disabled:cursor-not-allowed"
            />
            {props.unit && (
              <span className="text-xs text-pf-text-muted px-1.5 py-1 bg-pf-border/50 rounded-sm min-w-[40px] text-center">
                {props.unit}
              </span>
            )}
          </div>
        );
      case 'select':
        return (
          /* eslint-disable-next-line local/pf-no-raw-html-controls -- OrcaSlicer-style compact dropdown */
          <select
            id={id}
            value={props.value}
            onChange={(e) => props.onChange(e.target.value)}
            disabled={disabled}
            className="w-32 px-2 py-1 text-sm bg-pf-panel border border-pf-border rounded
                       text-pf-text cursor-pointer focus:border-pf-accent-2 focus:outline-hidden
                       disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {props.options.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        );
      case 'checkbox':
        return (
          <Checkbox
            id={id}
            checked={props.checked}
            onChange={(e: React.ChangeEvent<HTMLInputElement>) => props.onChange(e.target.checked)}
            disabled={disabled}
          />
        );
      case 'text':
        return (
          <input
            id={id}
            type="text"
            value={props.value}
            onChange={(e) => props.onChange(e.target.value)}
            placeholder={props.placeholder}
            disabled={disabled}
            className="w-32 px-2 py-1 text-sm bg-pf-panel border border-pf-border rounded
                       text-pf-text focus:border-pf-accent-2 focus:outline-hidden
                       disabled:opacity-50 disabled:cursor-not-allowed"
          />
        );
      default:
        return null;
    }
  };

  return (
    <div className={`flex items-center justify-between py-1.5 ${disabled ? 'opacity-50' : ''}`}>
      {/* Label with optional reset button */}
      <div className="flex items-center gap-1.5">
        {/* Reset button - shown when setting is modified */}
        {isModified && onReset && (
          <Button
            variant="subtle"
            type="button"
            onClick={onReset}
            className="p-0.5 text-pf-warning hover:text-pf-warning transition-colors
                       hover:bg-pf-warning/10 rounded"
            title={`Reset to original: ${formatOriginalValue(originalValue)}`}
            aria-label={`Reset ${label} to original value`}
          >
            <ResetIcon className="w-3.5 h-3.5" />
          </Button>
        )}
        
        <label 
          htmlFor={id} 
          className={`text-sm transition-colors ${
            isModified 
              ? 'text-pf-warning font-medium' 
              : 'text-pf-text'
          }`}
          title={tooltip}
        >
          {label}
        </label>
      </div>
      
      {/* Control */}
      {renderControl()}
    </div>
  );
};

/**
 * Section header for grouping compact settings
 */
interface SettingSectionProps {
  icon?: React.ReactNode;
  title: string;
  children: React.ReactNode;
}

export const SettingSection: React.FC<SettingSectionProps> = ({ icon, title, children }) => (
  <div className="mb-4">
    <div className="flex items-center gap-2 mb-2 pb-1 border-b border-pf-border/50">
      {icon && <span className="text-pf-accent-2">{icon}</span>}
      <h4 className="text-sm font-semibold text-pf-text">{title}</h4>
    </div>
    <div className="space-y-0.5 pl-1">
      {children}
    </div>
  </div>
);

export default SettingRow;
