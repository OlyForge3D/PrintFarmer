/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import { SettingInputType } from '@/types/SettingInputType';
import { InfoIcon } from '@/common/components/icons/MdiIcons';

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
}

// Helper
function getInputValue(val: SettingValue): string | number | '' {
  if (typeof val === 'number' || typeof val === 'string') return val as string | number;
  return '';
}

/** Inline info tooltip button - shows description on hover */
const InfoTooltip: React.FC<{ description: string }> = ({ description }) => (
  <span
    className="inline-flex items-center ml-1.5 text-pf-text-secondary hover:text-pf-accent cursor-help transition-colors"
    title={description}
    aria-label={description}
  >
    <InfoIcon className="w-4 h-4" />
  </span>
);

export const SettingsPagelet: React.FC<SettingsPageletProps> = ({ metadata, values, onChange, fieldErrors, error }) => {
  return (
    <div className="settings-pagelet bg-pf-bg-1 border border-pf-border rounded-xl p-4 mb-6 shadow-sm">
      <h3 className="text-lg font-semibold text-pf-text-primary mb-3">{metadata.displayName || metadata.className}</h3>
      <div className="space-y-2">
            {metadata.properties.map((prop0: SettingPropertyMetadata) => {
              // Narrow prop to allow legacy fields without using `any`
              const prop = prop0 as SettingPropertyMetadata & { displayName?: string; required?: boolean };
              // Support legacy shapes: prop.display?.name OR prop.displayName
              const displayName = (prop.display && (prop.display.name as string | undefined)) || prop.displayName || prop.name;
              const isRequired = (prop.attributes && prop.attributes.includes('RequiredAttribute')) || Boolean(prop.required);
              const err = fieldErrors?.[prop.name];
              const hasDescription = Boolean(prop.display?.description);

          return (
            <div className="flex items-center gap-3 py-1" key={prop.name}>
              {/* Label column - fixed width for alignment */}
              <label 
                className="flex items-center flex-shrink-0 w-48 text-sm font-medium text-pf-text-primary" 
                htmlFor={prop.name}
              >
                <span className="truncate">{displayName}</span>
                {isRequired && <span className="text-pf-accent ml-1">*</span>}
                {hasDescription && <InfoTooltip description={prop.display!.description!} />}
              </label>

              {/* Input column - flexible width */}
              {prop.display?.inputType === SettingInputType.Array && prop.display?.isMulti && Array.isArray(values[prop.name]) ? (
                <div className="flex-1 min-w-0">
                  {(values[prop.name] as (string | number)[]).map((val, idx) => (
                    <div key={idx} className="flex items-center mb-1.5">
                      <input
                        type={typeof val === 'number' ? 'number' : 'text'}
                        className="border border-pf-border rounded px-2 py-1.5 flex-1 text-sm text-pf-text-primary bg-pf-bg-2 focus:border-pf-accent focus:ring-1 focus:ring-pf-accent/30 transition"
                        value={typeof val === 'number' ? val : (typeof val === 'string' ? val : '')}
                        placeholder={displayName}
                        title={prop.display?.description || displayName}
                        aria-label={displayName}
                        onChange={e => {
                          const arr = Array.isArray(values[prop.name]) ? [...(values[prop.name] as (string | number)[])] : [];
                          arr[idx] = typeof val === 'number' ? Number(e.currentTarget.value) : e.currentTarget.value;
                          onChange(prop.name, arr);
                        }}
                      />
                      <button
                        type="button"
                        className="ml-1.5 px-2 py-1 text-xs bg-pf-error/90 hover:bg-pf-error text-white rounded transition-colors"
                        onClick={() => {
                          const arr = Array.isArray(values[prop.name]) ? [...(values[prop.name] as (string | number)[])] : [];
                          arr.splice(idx, 1);
                          onChange(prop.name, arr);
                        }}
                        aria-label={`Remove ${prop.display?.name || prop.name}`}
                      >×</button>
                    </div>
                  ))}
                  <button
                    type="button"
                    className="px-2 py-1 text-xs bg-pf-accent/90 hover:bg-pf-accent text-white rounded transition-colors"
                    onClick={() => {
                      const arr = Array.isArray(values[prop.name]) ? [...(values[prop.name] as (string | number)[])] : [];
                      arr.push(Array.isArray(values[prop.name]) && typeof (values[prop.name] as (string | number)[])[0] === 'number' ? 0 : '');
                      onChange(prop.name, arr);
                    }}
                    aria-label={`Add ${prop.display?.name || prop.name}`}
                  >+ Add</button>
                  {err && <div className="text-pf-error text-xs mt-1">{err}</div>}
                </div>
              ) : prop.display?.inputType === SettingInputType.Boolean || prop.type === 'Boolean' || prop.type === 'bool' ? (
                <div className="flex-1 min-w-0">
                  <input
                    id={prop.name}
                    name={prop.name}
                    type="checkbox"
                    className="h-4 w-4 accent-pf-accent border-pf-border rounded focus:ring-pf-accent"
                    checked={Boolean(values[prop.name])}
                    onChange={e => onChange(prop.name, e.currentTarget.checked)}
                  />
                  {err && <div className="text-pf-error text-xs mt-1">{err}</div>}
                </div>
              ) : prop.display?.inputType === SettingInputType.TextArea ? (
                <div className="flex-1 min-w-0">
                  <textarea
                    id={prop.name}
                    name={prop.name}
                    className="border border-pf-border rounded px-2 py-1.5 w-full text-sm text-pf-text-primary bg-pf-bg-2 focus:border-pf-accent focus:ring-1 focus:ring-pf-accent/30 transition resize-none"
                    rows={2}
                    value={String(getInputValue(values[prop.name] as SettingValue))}
                    onChange={e => onChange(prop.name, e.currentTarget.value)}
                    placeholder={displayName}
                    title={prop.display?.description || displayName}
                    aria-label={displayName}
                  />
                  {err && <div className="text-pf-error text-xs mt-1">{err}</div>}
                </div>
              ) : prop.display?.inputType === SettingInputType.Number || prop.type === 'number' || prop.type === 'Number' || prop.type === 'Int32' || prop.type === 'Int64' || prop.type === 'Double' || prop.type === 'Single' || prop.type === 'Decimal' ? (
                <div className="flex-1 min-w-0">
                  <input
                    id={prop.name}
                    name={prop.name}
                    type="number"
                    className="border border-pf-border rounded px-2 py-1.5 w-full text-sm text-pf-text-primary bg-pf-bg-2 focus:border-pf-accent focus:ring-1 focus:ring-pf-accent/30 transition"
                    value={getInputValue(values[prop.name] as SettingValue)}
                    min={prop.display?.minValue}
                    max={prop.display?.maxValue}
                    step={prop.type === 'Double' || prop.type === 'Single' || prop.type === 'Decimal' ? 'any' : '1'}
                    onChange={e => onChange(prop.name, e.currentTarget.value === '' ? '' : Number(e.currentTarget.value))}
                    placeholder={displayName}
                    title={prop.display?.description || displayName}
                    aria-label={displayName}
                  />
                  {err && <div className="text-pf-error text-xs mt-1">{err}</div>}
                </div>
              ) : prop.display?.inputType === SettingInputType.Select && Array.isArray(prop.display?.allowedValues) ? (
                <div className="flex-1 min-w-0">
                  <select
                    id={prop.name}
                    name={prop.name}
                    className="border border-pf-border rounded px-2 py-1.5 w-full text-sm text-pf-text-primary bg-pf-bg-2 focus:border-pf-accent focus:ring-1 focus:ring-pf-accent/30 transition"
                    value={String(getInputValue(values[prop.name] as SettingValue))}
                    onChange={e => onChange(prop.name, e.currentTarget.value)}
                    aria-label={displayName}
                  >
                    <option value="">Select...</option>
                    {prop.display!.allowedValues!.map((opt, idx) => (
                      <option key={idx} value={String(opt)}>{String(opt)}</option>
                    ))}
                  </select>
                  {err && <div className="text-pf-error text-xs mt-1">{err}</div>}
                </div>
              ) : (
                <div className="flex-1 min-w-0">
                  <input
                    id={prop.name}
                    name={prop.name}
                    type={prop.display?.inputType === SettingInputType.Password ? 'password' : 'text'}
                    className="border border-pf-border rounded px-2 py-1.5 w-full text-sm text-pf-text-primary bg-pf-bg-2 focus:border-pf-accent focus:ring-1 focus:ring-pf-accent/30 transition"
                    value={String(getInputValue(values[prop.name] as SettingValue))}
                    onChange={e => onChange(prop.name, e.currentTarget.value)}
                    placeholder={displayName}
                    title={prop.display?.description || displayName}
                    aria-label={displayName}
                  />
                  {err && <div className="text-pf-error text-xs mt-1">{err}</div>}
                </div>
              )}
            </div>
          );
        })}

  {/* top-level save error (if any) */}
  {error && <div className="text-pf-error font-medium text-sm mb-2">{error}</div>}

        {/* Dynamic PerEngine Slicer Settings UI */}
        {metadata.className === 'SlicerSettings' && values['PerEngine'] && typeof values['PerEngine'] === 'object' && (
          <div className="mt-4 pt-3 border-t border-pf-border">
            <h4 className="text-base font-semibold mb-2">Per-Engine Slicer Settings</h4>
            {Object.entries(values['PerEngine'] as Record<string, unknown>).map(([engine, engineSettings]) => (
              <div key={engine} className="border rounded p-3 mb-3 bg-pf-bg-2">
                <h5 className="font-medium text-sm mb-2">{engine}</h5>
                <div className="space-y-1.5">
                  {Object.entries(engineSettings as Record<string, string | number | boolean | undefined>).map(([field, value]) => (
                    <div className="flex items-center gap-3" key={field}>
                      <label className="flex-shrink-0 w-32 text-sm font-medium" htmlFor={`perengine-${engine}-${field}`}>{field}</label>
                      <input
                        id={`perengine-${engine}-${field}`}
                        className="border border-pf-border rounded px-2 py-1.5 flex-1 text-sm bg-pf-bg-1"
                        value={typeof value === 'string' || typeof value === 'number' ? value : ''}
                        placeholder={field}
                        title={field}
                        onChange={e => {
                          // Defensive: ensure engineSettings and values['PerEngine'] are objects
                          const currentEngineSettings = typeof engineSettings === 'object' && engineSettings !== null ? engineSettings : {};
                          const currentPerEngine = typeof values['PerEngine'] === 'object' && values['PerEngine'] !== null ? values['PerEngine'] : {};
                          const updated = {
                            ...currentPerEngine,
                            [engine]: {
                              ...currentEngineSettings,
                              [field]: e.target.value
                            }
                          };
                          onChange('PerEngine', updated);
                        }}
                      />
                    </div>
                  ))}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
};

export default SettingsPagelet;
