import { useState, useMemo } from 'react';
import type { ProfileFieldMetadata } from '@/types/api';
import { SchemaField } from './SchemaField';
import { SettingSection } from '../SettingRow';
import { Button } from '@/common/components/ui';

interface SchemaCategoryPanelProps {
  category: string;
  categoryLabel: string;
  fields: ProfileFieldMetadata[];
  values: Record<string, unknown>;
  onChange: (key: string, value: unknown) => void;
  disabled?: boolean;
  hasChanges?: (key: string) => boolean;
  onReset?: (key: string) => void;
  getOriginalValue?: (key: string) => unknown;
}

export function SchemaCategoryPanel({
  category,
  categoryLabel,
  fields,
  values,
  onChange,
  disabled = false,
  hasChanges,
  onReset,
  getOriginalValue,
}: SchemaCategoryPanelProps) {
  const [showAdvanced, setShowAdvanced] = useState(false);

  const { basicFields, advancedFields } = useMemo(() => {
    const categoryFields = fields.filter(f => f.category === category);
    return {
      basicFields: categoryFields.filter(f => !f.isAdvanced),
      advancedFields: categoryFields.filter(f => f.isAdvanced),
    };
  }, [fields, category]);

  if (basicFields.length === 0 && advancedFields.length === 0) {
    return null;
  }

  return (
    <div className="space-y-4">
      {basicFields.length > 0 && (
        <SettingSection title={categoryLabel}>
          {basicFields.map(field => (
            <SchemaField
              key={field.key}
              field={field}
              value={values[field.key]}
              onChange={onChange}
              disabled={disabled}
              isModified={hasChanges?.(field.key) ?? false}
              onReset={onReset ? () => onReset(field.key) : undefined}
              originalValue={getOriginalValue?.(field.key)}
            />
          ))}
        </SettingSection>
      )}

      {advancedFields.length > 0 && (
        <div className="space-y-3">
          <Button
            variant="subtle"
            size="sm"
            onClick={() => setShowAdvanced(!showAdvanced)}
            className="text-pf-text-secondary"
          >
            {showAdvanced ? '▼' : '▶'} Show Advanced ({advancedFields.length})
          </Button>

          {showAdvanced && (
            <SettingSection title={`${categoryLabel} — Advanced`}>
              {advancedFields.map(field => (
                <SchemaField
                  key={field.key}
                  field={field}
                  value={values[field.key]}
                  onChange={onChange}
                  disabled={disabled}
                  isModified={hasChanges?.(field.key) ?? false}
                  onReset={onReset ? () => onReset(field.key) : undefined}
                  originalValue={getOriginalValue?.(field.key)}
                />
              ))}
            </SettingSection>
          )}
        </div>
      )}
    </div>
  );
}
