import React from 'react';
import { PrinterBackend, PrinterBackendString } from '@/types/api';
import { getPrinterBackendOptions, getPrinterBackendStringOptions, toPrinterBackend } from '@/common/utils/enumHelpers';
import { Select } from '@/common/components/ui';

/**
 * Props for BackendSelector when the caller holds a `PrinterBackend` value.
 * Used by physical printer editing.
 *
 * NOTE: `PrinterBackend` is a *string* enum matching the wire contract; the
 * `'numeric'` discriminator name is historical. It now selects only which
 * TypeScript type is handed back to `onChange`, not the runtime representation.
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
 * Supports two modes, which differ only in the TypeScript type handed to
 * `onChange` - both emit PascalCase strings at runtime:
 * - valueType='numeric' (default): hands back `PrinterBackend` (for physical printers)
 * - valueType='string': hands back `PrinterBackendString` (for printer models)
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
    
    // Treat "Unknown" as undefined (unset) since it's not a valid selectable backend
    const effectiveValue = value === 'Unknown' as unknown ? undefined : value;
    
    const handleChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
      const newValue = e.target.value === '' ? undefined : e.target.value as PrinterBackendString;
      onChange(newValue);
    };

    return (
      <Select
        value={effectiveValue ?? ''}
        onChange={handleChange}
        aria-label={ariaLabel}
        title={ariaLabel}
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

  // Numeric mode (default).
  // NOTE: PrinterBackend is now a string enum matching the wire contract, so this
  // mode differs from 'string' mode only in the type it hands back to onChange.
  const { value, onChange } = props as NumericBackendSelectorProps;

  const handleChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    const newValue = e.target.value === '' ? undefined : toPrinterBackend(e.target.value);
    onChange(newValue);
  };

  return (
    <Select
      value={value ?? ''}
      onChange={handleChange}
      aria-label={ariaLabel}
      title={ariaLabel}
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
