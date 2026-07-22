import React, { useState, useCallback, useMemo } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Input, Select, Toggle, FormField, Spinner } from '@/common/components/ui';
import { SaveIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { useCustomFieldDefinitions, useCustomFieldValues, queryKeys } from '@/common/hooks/useApi';
import { toast } from 'sonner';
import type { CustomFieldEntityType, CustomFieldValue } from '@/types/api';

interface CustomFieldsSectionProps {
  entityType: CustomFieldEntityType;
  entityId: string;
  editable?: boolean;
}

export function CustomFieldsSection({ entityType, entityId, editable = false }: CustomFieldsSectionProps) {
  const queryClient = useQueryClient();
  const { data: definitions = [], isLoading: defsLoading } = useCustomFieldDefinitions(entityType);
  const { data: fieldValues = [], isLoading: valsLoading } = useCustomFieldValues(entityType, entityId);

  const serverValues = useMemo(() => {
    const map: Record<string, string | null> = {};
    for (const fv of fieldValues) {
      map[fv.definitionId] = fv.value ?? null;
    }
    return map;
  }, [fieldValues]);

  const [overrides, setOverrides] = useState<Record<string, string | null>>({});

  // Reset dirty overrides when the entity changes
  // (React-recommended pattern for adjusting state when props change)
  const [prevEntityKey, setPrevEntityKey] = useState(`${entityType}:${entityId}`);
  const currentEntityKey = `${entityType}:${entityId}`;
  if (prevEntityKey !== currentEntityKey) {
    setPrevEntityKey(currentEntityKey);
    setOverrides({});
  }

  const localValues = useMemo(() => ({ ...serverValues, ...overrides }), [serverValues, overrides]);
  const dirty = Object.keys(overrides).length > 0;

  const saveMutation = useMutation({
    mutationFn: () => apiClient.setCustomFieldValues(entityType, entityId, localValues),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.customFieldValues(entityType, entityId) });
      toast.success('Custom fields saved');
      setOverrides({});
    },
    onError: (err: Error) => toast.error(`Failed to save: ${err.message}`),
  });

  const updateValue = useCallback((defId: string, value: string | null) => {
    setOverrides(prev => ({ ...prev, [defId]: value }));
  }, []);

  if (defsLoading || valsLoading) {
    return <Spinner size="sm" />;
  }

  if (definitions.length === 0) {
    return null;
  }

  const renderValues = fieldValues.length > 0 ? fieldValues : definitions.map(d => ({
    definitionId: d.id,
    fieldName: d.fieldName,
    fieldKey: d.fieldKey,
    fieldType: d.fieldType,
    value: undefined,
    options: d.options,
    isRequired: d.isRequired,
  } satisfies CustomFieldValue));

  return (
    <div className="space-y-4">
      <h3 className="text-sm font-medium text-pf-text-secondary uppercase tracking-wider">Custom Fields</h3>
      <div className="grid gap-3 sm:grid-cols-2">
        {renderValues.map(fv => (
          <FieldRenderer
            key={fv.definitionId}
            field={fv}
            value={localValues[fv.definitionId] ?? fv.value ?? null}
            editable={editable}
            onChange={val => updateValue(fv.definitionId, val)}
          />
        ))}
      </div>
      {editable && dirty && (
        <div className="pt-2">
          <Button
            variant="primary"
            size="sm"
            onClick={() => saveMutation.mutate()}
            loading={saveMutation.isPending}
            iconLeft={<SaveIcon className="h-4 w-4" />}
          >
            Save Custom Fields
          </Button>
        </div>
      )}
    </div>
  );
}

interface FieldRendererProps {
  field: CustomFieldValue;
  value: string | null;
  editable: boolean;
  onChange: (value: string | null) => void;
}

function FieldRenderer({ field, value, editable, onChange }: FieldRendererProps) {
  if (!editable) {
    return (
      <div>
        <div className="text-xs text-pf-text-secondary mb-0.5">{field.fieldName}</div>
        <div className="text-sm text-pf-text-primary">
          {field.fieldType === 'Boolean'
            ? (value === 'true' ? 'Yes' : 'No')
            : (value || '—')}
        </div>
      </div>
    );
  }

  switch (field.fieldType) {
    case 'Text':
      return (
        <FormField label={field.fieldName} htmlFor={`cf-${field.fieldKey}`} required={field.isRequired}>
          <Input
            id={`cf-${field.fieldKey}`}
            value={value ?? ''}
            onChange={e => onChange(e.target.value || null)}
          />
        </FormField>
      );
    case 'Number':
      return (
        <FormField label={field.fieldName} htmlFor={`cf-${field.fieldKey}`} required={field.isRequired}>
          <Input
            id={`cf-${field.fieldKey}`}
            type="number"
            value={value ?? ''}
            onChange={e => onChange(e.target.value || null)}
          />
        </FormField>
      );
    case 'Boolean':
      return (
        <div className="flex items-center gap-2 pt-5">
          <Toggle
            checked={value === 'true'}
            onChange={checked => onChange(checked ? 'true' : 'false')}
          />
          <span className="text-sm text-pf-text-primary">{field.fieldName}</span>
        </div>
      );
    case 'Date':
      return (
        <FormField label={field.fieldName} htmlFor={`cf-${field.fieldKey}`} required={field.isRequired}>
          <Input
            id={`cf-${field.fieldKey}`}
            type="date"
            value={value ?? ''}
            onChange={e => onChange(e.target.value || null)}
          />
        </FormField>
      );
    case 'Select': {
      const opts = parseSelectOptions(field.options);
      return (
        <FormField label={field.fieldName} htmlFor={`cf-${field.fieldKey}`} required={field.isRequired}>
          <Select
            id={`cf-${field.fieldKey}`}
            value={value ?? ''}
            onChange={e => onChange(e.target.value || null)}
          >
            <option value="">— Select —</option>
            {opts.map(opt => (
              <option key={opt} value={opt}>{opt}</option>
            ))}
          </Select>
        </FormField>
      );
    }
    default:
      return null;
  }
}

function parseSelectOptions(optionsJson?: string): string[] {
  if (!optionsJson) return [];
  try {
    const arr: unknown = JSON.parse(optionsJson);
    return Array.isArray(arr) ? arr.filter((x): x is string => typeof x === 'string') : [];
  } catch {
    return [];
  }
}
