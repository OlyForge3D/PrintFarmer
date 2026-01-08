/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import { SettingInputType } from '@/types/SettingInputType';

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

export const SettingsPagelet: React.FC<SettingsPageletProps> = ({ metadata, values, onChange, fieldErrors, error }) => {
  return (
    <div className="settings-pagelet bg-pf-bg-1 border border-pf-border rounded-xl p-6 mb-8 shadow-sm">
      <h3 className="text-xl font-semibold text-pf-text-primary mb-4">{metadata.displayName || metadata.className}</h3>
      <div>
            {metadata.properties.map((prop0: SettingPropertyMetadata) => {
              // Narrow prop to allow legacy fields without using `any`
              const prop = prop0 as SettingPropertyMetadata & { displayName?: string; required?: boolean };
              // Support legacy shapes: prop.display?.name OR prop.displayName
              const displayName = (prop.display && (prop.display.name as string | undefined)) || prop.displayName || prop.name;
              const isRequired = (prop.attributes && prop.attributes.includes('RequiredAttribute')) || Boolean(prop.required);
              const err = fieldErrors?.[prop.name];
          return (
            <div className="mb-5" key={prop.name}>
                    <label className="block font-medium text-pf-text-primary mb-1" htmlFor={prop.name}>
                      {displayName}
                      {isRequired && (
                        <span className="text-xs text-pf-accent ml-2">*</span>
                      )}
                      {prop.display?.description && (
                        <span className="block text-xs text-pf-text-secondary mt-1">{prop.display.description}</span>
                      )}
                    </label>

              {prop.display?.inputType === SettingInputType.Array && prop.display?.isMulti && Array.isArray(values[prop.name]) ? (
                <div>
                  {(values[prop.name] as (string | number)[]).map((val, idx) => (
                    <div key={idx} className="flex items-center mb-2">
                      <input
                        type={typeof val === 'number' ? 'number' : 'text'}
                        className="border border-pf-border rounded px-3 py-2 w-full text-pf-text-primary bg-pf-bg-2 focus:border-pf-accent focus:ring-2 focus:ring-pf-accent/30 transition"
                        value={typeof val === 'number' ? val : (typeof val === 'string' ? val : '')}
                        placeholder={prop.display?.description || displayName}
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
                        className="ml-2 px-2 py-1 text-xs bg-pf-error text-white rounded"
                        onClick={() => {
                          const arr = Array.isArray(values[prop.name]) ? [...(values[prop.name] as (string | number)[])] : [];
                          arr.splice(idx, 1);
                          onChange(prop.name, arr);
                        }}
                        aria-label={`Remove ${prop.display?.name || prop.name}`}
                      >Remove</button>
                    </div>
                  ))}
                  <button
                    type="button"
                    className="mt-2 px-3 py-1 bg-pf-accent text-white rounded"
                    onClick={() => {
                      const arr = Array.isArray(values[prop.name]) ? [...(values[prop.name] as (string | number)[])] : [];
                      arr.push(Array.isArray(values[prop.name]) && typeof (values[prop.name] as (string | number)[])[0] === 'number' ? 0 : '');
                      onChange(prop.name, arr);
                    }}
                    aria-label={`Add ${prop.display?.name || prop.name}`}
                  >Add Value</button>
                  {err && <div className="text-pf-error text-sm mt-1">{err}</div>}
                  {err && <div className="text-pf-error text-sm mt-1">{err}</div>}
                </div>
              ) : prop.display?.inputType === SettingInputType.Boolean || prop.type === 'Boolean' || prop.type === 'bool' ? (
                <div>
                  <input
                    id={prop.name}
                    name={prop.name}
                    type="checkbox"
                    className="h-4 w-4 accent-pf-accent border-pf-border rounded focus:ring-pf-accent"
                    checked={Boolean(values[prop.name])}
                    onChange={e => onChange(prop.name, e.currentTarget.checked)}
                  />
                  {err && <div className="text-pf-error text-sm mt-1">{err}</div>}
                </div>
              ) : prop.display?.inputType === SettingInputType.Text ? (
                <div>
                  <textarea
                    id={prop.name}
                    name={prop.name}
                    className="border border-pf-border rounded px-3 py-2 w-full text-pf-text-primary bg-pf-bg-2 focus:border-pf-accent focus:ring-2 focus:ring-pf-accent/30 transition"
                    value={String(getInputValue(values[prop.name] as SettingValue))}
                    onChange={e => onChange(prop.name, e.currentTarget.value)}
                    placeholder={prop.display?.description || displayName}
                    title={prop.display?.description || displayName}
                    aria-label={displayName}
                  />
                  {err && <div className="text-pf-error text-sm mt-1">{err}</div>}
                </div>
              ) : prop.display?.inputType === SettingInputType.Number || prop.type === 'number' || prop.type === 'Number' ? (
                <div>
                  <input
                    id={prop.name}
                    name={prop.name}
                    type="number"
                    className="border border-pf-border rounded px-3 py-2 w-full text-pf-text-primary bg-pf-bg-2 focus:border-pf-accent focus:ring-2 focus:ring-pf-accent/30 transition"
                    value={getInputValue(values[prop.name] as SettingValue)}
                    min={prop.display?.minValue}
                    max={prop.display?.maxValue}
                    onChange={e => onChange(prop.name, e.currentTarget.value === '' ? '' : Number(e.currentTarget.value))}
                    placeholder={prop.display?.description || displayName}
                    title={prop.display?.description || displayName}
                    aria-label={displayName}
                  />
                  {err && <div className="text-pf-error text-sm mt-1">{err}</div>}
                </div>
              ) : prop.display?.inputType === SettingInputType.Select && Array.isArray(prop.display?.allowedValues) ? (
                <div>
                  <select
                    id={prop.name}
                    name={prop.name}
                    className="border border-pf-border rounded px-3 py-2 w-full text-pf-text-primary bg-pf-bg-2 focus:border-pf-accent focus:ring-2 focus:ring-pf-accent/30 transition"
                    value={String(getInputValue(values[prop.name] as SettingValue))}
                    onChange={e => onChange(prop.name, e.currentTarget.value)}
                    aria-label={displayName}
                  >
                    <option value="">Select...</option>
                    {prop.display!.allowedValues!.map((opt, idx) => (
                      <option key={idx} value={String(opt)}>{String(opt)}</option>
                    ))}
                  </select>
                  {err && <div className="text-pf-error text-sm mt-1">{err}</div>}
                </div>
              ) : (
                <div>
                  <input
                    id={prop.name}
                    name={prop.name}
                    type={prop.display?.inputType === SettingInputType.Password ? 'password' : 'text'}
                    className="border border-pf-border rounded px-3 py-2 w-full text-pf-text-primary bg-pf-bg-2 focus:border-pf-accent focus:ring-2 focus:ring-pf-accent/30 transition"
                    value={String(getInputValue(values[prop.name] as SettingValue))}
                    onChange={e => onChange(prop.name, e.currentTarget.value)}
                    placeholder={prop.display?.description || displayName}
                    title={prop.display?.description || displayName}
                    aria-label={displayName}
                  />
                  {err && <div className="text-pf-error text-sm mt-1">{err}</div>}
                </div>
              )}
            </div>
          );
        })}

  {/* top-level save error (if any) */}
  {error && <div className="text-pf-error font-medium mb-3">{error}</div>}

        {/* Dynamic PerEngine Slicer Settings UI */}
        {metadata.className === 'SlicerSettings' && values['PerEngine'] && typeof values['PerEngine'] === 'object' && (
          <div className="mt-6">
            <h4 className="text-lg font-semibold mb-2">Per-Engine Slicer Settings</h4>
            {Object.entries(values['PerEngine'] as Record<string, unknown>).map(([engine, engineSettings]) => (
              <div key={engine} className="border rounded p-4 mb-4 bg-pf-bg-2">
                <h5 className="font-bold mb-2">{engine}</h5>
                {Object.entries(engineSettings as Record<string, string | number | boolean | undefined>).map(([field, value]) => (
                  <div className="mb-3" key={field}>
                    <label className="block font-medium mb-1" htmlFor={`perengine-${engine}-${field}`}>{field}</label>
                    <input
                      id={`perengine-${engine}-${field}`}
                      className="border rounded px-2 py-1 w-full"
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
            ))}
          </div>
        )}
      </div>
    </div>
  );
};

export default SettingsPagelet;
