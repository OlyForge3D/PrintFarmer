import { useCallback } from 'react';
import type { ProfileFieldMetadata } from '@/types/api';
import { SettingRow } from '../SettingRow';

interface SchemaFieldProps {
  field: ProfileFieldMetadata;
  value: unknown;
  onChange: (key: string, value: unknown) => void;
  disabled?: boolean;
  isModified?: boolean;
  onReset?: () => void;
}

export function SchemaField({
  field,
  value,
  onChange,
  disabled = false,
  isModified = false,
  onReset,
}: SchemaFieldProps) {
  const handleChange = useCallback(
    (newValue: unknown) => {
      onChange(field.key, newValue);
    },
    [field.key, onChange]
  );

  const displayValue = value ?? field.defaultValue ?? '';

  switch (field.fieldType) {
    case 'boolean':
      return (
        <SettingRow
          type="checkbox"
          label={field.label}
          checked={Boolean(displayValue)}
          onChange={(checked) => handleChange(checked)}
          disabled={disabled}
          isModified={isModified}
          onReset={onReset}
        />
      );

    case 'number':
    case 'integer':
      return (
        <SettingRow
          type="slider"
          label={field.label}
          value={Number(displayValue) || 0}
          onChange={(nextValue) => handleChange(nextValue)}
          min={field.min ?? 0}
          max={field.max ?? 100}
          step={field.step ?? (field.fieldType === 'integer' ? 1 : 0.1)}
          disabled={disabled}
          unit={field.unit}
          isModified={isModified}
          onReset={onReset}
        />
      );

    case 'enum':
      return (
        <SettingRow
          type="select"
          label={field.label}
          value={String(displayValue)}
          onChange={(nextValue) => handleChange(nextValue)}
          options={field.options?.map(opt => ({ value: opt.value, label: opt.label })) ?? []}
          disabled={disabled}
          isModified={isModified}
          onReset={onReset}
        />
      );

    case 'string':
    default:
      return (
        <SettingRow
          type="text"
          label={field.label}
          value={String(displayValue)}
          onChange={(nextValue) => handleChange(nextValue)}
          disabled={disabled}
          isModified={isModified}
          onReset={onReset}
        />
      );
  }
}
