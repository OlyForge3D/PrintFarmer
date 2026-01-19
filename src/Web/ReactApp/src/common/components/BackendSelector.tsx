import React from 'react';
import { PrinterBackend, PrinterBackendString } from '@/types/api';
import { getPrinterBackendOptions, getPrinterBackendStringOptions } from '@/common/utils/enumHelpers';
import { Select } from '@/common/components/ui';

/**
 * Props for BackendSelector when using numeric enum values (PrinterBackend)
 * Used by physical printer editing (Printer uses numeric backend)
 */
interface NumericBackendSelectorProps {
  value: PrinterBackend | undefined;
  onChange: (backend: PrinterBackend | undefined) => void;
  valueType?: 'numeric';
  className?: string;
  placeholder?: string;
  required?: boolean;
  disabled?: boolean;
  ariaLabel?: string;
}

/**
 * Props for BackendSelector when using string values (PrinterBackendString)
 * Used by printer model editing (API returns/expects string enum values)
 */
interface StringBackendSelectorProps {
  value: PrinterBackendString | undefined;
  onChange: (backend: PrinterBackendString | undefined) => void;
  valueType: 'string';
  className?: string;
  placeholder?: string;
  required?: boolean;
  disabled?: boolean;
  ariaLabel?: string;
}

type BackendSelectorProps = NumericBackendSelectorProps | StringBackendSelectorProps;

/**
 * Reusable backend selector component that automatically includes all PrinterBackend enum values.
 * When new backends are added to the enum, they will automatically appear in this dropdown.
 * 
 * Supports two modes:
 * - valueType='numeric' (default): Uses PrinterBackend numeric enum (for physical printers)
 * - valueType='string': Uses PrinterBackendString (for printer models, API compatibility)
 * 
 * Renders as a bare Select element without FormField wrapper for flexible layout.
 */
export function BackendSelector(props: BackendSelectorProps) {
  const {
    className,
    placeholder = 'Select backend...',
    required = false,
    disabled = false,
    ariaLabel = 'Backend type',
    valueType = 'numeric',
  } = props;

  if (valueType === 'string') {
    const { value, onChange } = props as StringBackendSelectorProps;
    
    const handleChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
      const newValue = e.target.value === '' ? undefined : e.target.value as PrinterBackendString;
      onChange(newValue);
    };

    return (
      <Select
        value={value ?? ''}
        onChange={handleChange}
        aria-label={ariaLabel}
        className={className}
        required={required}
        disabled={disabled}
      >
        {!required && <option value="">{placeholder}</option>}
        {getPrinterBackendStringOptions().map(option => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </Select>
    );
  }

  // Numeric mode (default)
  const { value, onChange } = props as NumericBackendSelectorProps;
  
  const handleChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    const newValue = e.target.value === '' ? undefined : parseInt(e.target.value, 10) as PrinterBackend;
    onChange(newValue);
  };

  return (
    <Select
      value={value ?? ''}
      onChange={handleChange}
      aria-label={ariaLabel}
      className={className}
      required={required}
      disabled={disabled}
    >
      {!required && <option value="">{placeholder}</option>}
      {getPrinterBackendOptions().map(option => (
        <option key={option.value} value={option.value}>
          {option.label}
        </option>
      ))}
    </Select>
  );
}
