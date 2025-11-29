import React from 'react';
import { PrinterBackend } from '@/types/api';
import { getPrinterBackendOptions } from '@/utils/enumHelpers';
import { Select, FormField } from '@/components/ui';

interface BackendSelectorProps {
  value: PrinterBackend | undefined;
  onChange: (backend: PrinterBackend | undefined) => void;
  className?: string;
  placeholder?: string;
  required?: boolean;
  disabled?: boolean;
  title?: string;
}

/**
 * Reusable backend selector component that automatically includes all PrinterBackend enum values.
 * When new backends are added to the enum, they will automatically appear in this dropdown.
 */
export function BackendSelector({
  value,
  onChange,
  className = '',
  placeholder = 'Select backend...',
  required = false,
  disabled = false,
  title = 'Backend',
}: BackendSelectorProps) {
  const handleChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    const newValue = e.target.value === '' ? undefined : parseInt(e.target.value, 10) as PrinterBackend;
    onChange(newValue);
  };

  return (
    <FormField label={title} className={className}>
      <Select
        value={value ?? ''}
        onChange={handleChange}
        title={title}
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
    </FormField>
  );
}
