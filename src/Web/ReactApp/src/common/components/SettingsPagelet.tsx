import React from 'react';
import { SettingInputType } from '@/types/SettingInputType';
import { InfoIcon, PlusIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button, Input, Select, Textarea, Checkbox } from '@/common/components/ui';
import { HighlightedText } from '@/features/admin/settings/HighlightedText';

export type SettingValue = string | number | boolean | string[] | number[] | (string | number)[] | Record<string, unknown> | undefined;

export interface SettingPropertyDisplayMetadata {
  name?: string;
  description?: string;
  icon?: string;
  group?: string;
  order?: number;
  inputType?: SettingInputType;
  isMulti?: boolean;
  allowedValues?: unknown[];
  minValue?: number;
  maxValue?: number;
}

export interface SettingPropertyMetadata {
  name: string;
  type: string;
  attributes: string[];
  display?: SettingPropertyDisplayMetadata;
}

export interface SettingMetadata {
  key: string;
  className: string;
  displayName?: string;
  description?: string;
  icon?: string;
  group?: string;
  order?: number;
  properties: SettingPropertyMetadata[];
}

export interface SettingsPageletProps {
  metadata: SettingMetadata;
  values: Record<string, SettingValue>;
  onChange: (field: string, value: SettingValue) => void;
  fieldErrors?: Record<string, string> | null;
  isSaving?: boolean;
  error?: string | null;
  /** When true, renders only fields without the outer card wrapper and title */
  compact?: boolean;
  /**
   * Optional case-insensitive substring to highlight in property labels. Empty
   * string / undefined renders labels unchanged. Only the visible label is
   * highlighted — descriptions live in the info tooltip's `title` attribute,
   * which cannot contain rich markup, so no highlighting is applied there.
   */
  searchQuery?: string;
}

// Helper — string / number are directly usable as input values; anything else
// (undefined, arrays, objects) is coerced to '' so React doesn't warn.
function getInputValue(val: SettingValue): string | number | '' {
  if (typeof val === 'number' || typeof val === 'string') return val as string | number;
  return '';
}

const InfoTooltip: React.FC<{ description: string }> = ({ description }) => (
  <span
    className="inline-flex items-center ml-1.5 text-pf-text-secondary hover:text-pf-accent cursor-help transition-colors"
    title={description}
    aria-label={description}
  >
    <InfoIcon className="w-4 h-4" />
  </span>
);

/**
 * Metadata-driven form renderer for a single settings section. Given the section
 * metadata and current values, this component renders each property as the
 * appropriate control from the shared UI library (`Input`, `Select`, `Textarea`,
 * `Checkbox`). It does NOT own state, dirty tracking, or save behaviour — those
 * belong to the parent (`SettingsPage`).
 *
 * Section-specific UI that doesn't fit the metadata (e.g. Obico's server table
 * or SlicerSettings' per-engine map) is contributed via the section-renderer
 * registry (`section-renderers.tsx`) and rendered by `SettingsPage`, not here.
 */
export const SettingsPagelet: React.FC<SettingsPageletProps> = ({ metadata, values, onChange, fieldErrors, error, compact, searchQuery }) => {
  const query = searchQuery ?? '';
  const content = (
    <div className="space-y-2">
      {metadata.properties.map((prop0: SettingPropertyMetadata) => {
        const prop = prop0 as SettingPropertyMetadata & { displayName?: string; required?: boolean };
        const displayName = (prop.display && (prop.display.name as string | undefined)) || prop.displayName || prop.name;
        const isRequired = (prop.attributes && prop.attributes.includes('RequiredAttribute')) || Boolean(prop.required);
        const err = fieldErrors?.[prop.name];
        const hasDescription = Boolean(prop.display?.description);
        const invalid = Boolean(err);

        const label = (
          <label
            className="flex items-center shrink-0 w-64 text-sm font-medium text-pf-text-primary"
            htmlFor={prop.name}
          >
            <span className="break-words">
              {query ? <HighlightedText text={displayName} query={query} /> : displayName}
            </span>
            {isRequired && <span className="text-pf-accent ml-1">*</span>}
            {hasDescription && <InfoTooltip description={prop.display!.description!} />}
          </label>
        );

        const isArray = prop.display?.inputType === SettingInputType.Array
          && prop.display?.isMulti
          && Array.isArray(values[prop.name]);
        const isBoolean = prop.display?.inputType === SettingInputType.Boolean
          || prop.type === 'Boolean'
          || prop.type === 'bool';
        const isTextArea = prop.display?.inputType === SettingInputType.TextArea;
        const isNumber = prop.display?.inputType === SettingInputType.Number
          || prop.type === 'number'
          || prop.type === 'Number'
          || prop.type === 'Int32'
          || prop.type === 'Int64'
          || prop.type === 'Double'
          || prop.type === 'Single'
          || prop.type === 'Decimal';
        const isSelect = prop.display?.inputType === SettingInputType.Select
          && Array.isArray(prop.display?.allowedValues);

        let control: React.ReactNode;

        if (isArray) {
          const arr = values[prop.name] as (string | number)[];
          control = (
            <div className="flex-1 min-w-0">
              {arr.map((val, idx) => (
                <div key={idx} className="flex items-center mb-1.5 gap-1.5">
                  <Input
                    type={typeof val === 'number' ? 'number' : 'text'}
                    value={typeof val === 'number' ? val : typeof val === 'string' ? val : ''}
                    placeholder={displayName}
                    title={prop.display?.description || displayName}
                    aria-label={`${displayName} ${idx + 1}`}
                    className="flex-1"
                    onChange={(e) => {
                      const next = Array.isArray(values[prop.name])
                        ? [...(values[prop.name] as (string | number)[])]
                        : [];
                      next[idx] = typeof val === 'number' ? Number(e.currentTarget.value) : e.currentTarget.value;
                      onChange(prop.name, next);
                    }}
                  />
                  <Button
                    type="button"
                    variant="secondary"
                    size="sm"
                    iconLeft={<CloseIcon className="w-3.5 h-3.5" />}
                    aria-label={`Remove ${displayName} ${idx + 1}`}
                    onClick={() => {
                      const next = Array.isArray(values[prop.name])
                        ? [...(values[prop.name] as (string | number)[])]
                        : [];
                      next.splice(idx, 1);
                      onChange(prop.name, next);
                    }}
                  />
                </div>
              ))}
              <Button
                type="button"
                variant="primary"
                size="sm"
                iconLeft={<PlusIcon className="w-3.5 h-3.5" />}
                aria-label={`Add ${displayName}`}
                onClick={() => {
                  const next = Array.isArray(values[prop.name])
                    ? [...(values[prop.name] as (string | number)[])]
                    : [];
                  const numeric = Array.isArray(values[prop.name])
                    && typeof (values[prop.name] as (string | number)[])[0] === 'number';
                  next.push(numeric ? 0 : '');
                  onChange(prop.name, next);
                }}
              >
                Add
              </Button>
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        } else if (isBoolean) {
          control = (
            <div className="flex-1 min-w-0">
              <Checkbox
                id={prop.name}
                name={prop.name}
                aria-label={displayName}
                checked={Boolean(values[prop.name])}
                invalid={invalid}
                onChange={(e) => onChange(prop.name, e.currentTarget.checked)}
              />
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        } else if (isTextArea) {
          control = (
            <div className="flex-1 min-w-0">
              <Textarea
                id={prop.name}
                name={prop.name}
                rows={2}
                value={String(getInputValue(values[prop.name] as SettingValue))}
                onChange={(e) => onChange(prop.name, e.currentTarget.value)}
                placeholder={displayName}
                title={prop.display?.description || displayName}
                aria-label={displayName}
                invalid={invalid}
              />
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        } else if (isNumber) {
          control = (
            <div className="flex-1 min-w-0">
              <Input
                id={prop.name}
                name={prop.name}
                type="number"
                value={getInputValue(values[prop.name] as SettingValue)}
                min={prop.display?.minValue}
                max={prop.display?.maxValue}
                step={prop.type === 'Double' || prop.type === 'Single' || prop.type === 'Decimal' ? 'any' : '1'}
                onChange={(e) => onChange(prop.name, e.currentTarget.value === '' ? '' : Number(e.currentTarget.value))}
                placeholder={displayName}
                title={prop.display?.description || displayName}
                aria-label={displayName}
                invalid={invalid}
              />
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        } else if (isSelect) {
          control = (
            <div className="flex-1 min-w-0">
              <Select
                id={prop.name}
                name={prop.name}
                value={String(getInputValue(values[prop.name] as SettingValue))}
                onChange={(e) => onChange(prop.name, e.currentTarget.value)}
                aria-label={displayName}
                invalid={invalid}
              >
                <option value="">Select...</option>
                {prop.display!.allowedValues!.map((opt, idx) => (
                  <option key={idx} value={String(opt)}>{String(opt)}</option>
                ))}
              </Select>
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        } else {
          control = (
            <div className="flex-1 min-w-0">
              <Input
                id={prop.name}
                name={prop.name}
                type={prop.display?.inputType === SettingInputType.Password ? 'password' : 'text'}
                value={String(getInputValue(values[prop.name] as SettingValue))}
                onChange={(e) => onChange(prop.name, e.currentTarget.value)}
                placeholder={displayName}
                title={prop.display?.description || displayName}
                aria-label={displayName}
                invalid={invalid}
              />
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        }

        return (
          <div
            className="flex items-center gap-3 py-1"
            key={prop.name}
            data-setting-property={`${metadata.key}.${prop.name}`}
          >
            {label}
            {control}
          </div>
        );
      })}

      {error && <div className="text-pf-error font-medium text-sm mb-2" role="alert">{error}</div>}
    </div>
  );

  if (compact) {
    return content;
  }

  return (
    <div className="settings-pagelet bg-pf-bg-1 border border-pf-border rounded-xl p-4 mb-6 shadow-xs">
      <h3 className="text-lg font-semibold text-pf-text-primary mb-3">{metadata.displayName || metadata.className}</h3>
      {content}
    </div>
  );
};

export default SettingsPagelet;
