import React, { useState } from 'react';

export interface SettingPropertyMetadata {
  name: string;
  type: string;
  attributes: string[];
  displayName?: string;
}

export interface SettingMetadata {
  key: string;
  className: string;
  displayName?: string;
  properties: SettingPropertyMetadata[];
}

export interface SettingsPageletProps {
  metadata: SettingMetadata;
  values: Record<string, any>;
  onChange: (field: string, value: any) => void;
  onSave: () => void;
  isSaving?: boolean;
  error?: string;
}

export const SettingsPagelet: React.FC<SettingsPageletProps> = ({
  metadata,
  values,
  onChange,
  onSave,
  isSaving,
  error
}) => {
  return (
    <div className="settings-pagelet border rounded p-4 mb-6 bg-white shadow">
      <h3 className="text-lg font-semibold mb-2">{metadata.displayName || metadata.className}</h3>
      <form
        onSubmit={e => {
          e.preventDefault();
          onSave();
        }}
      >
        {metadata.properties.map((prop) => (
          <div className="mb-4" key={prop.name}>
            <label className="block font-medium mb-1" htmlFor={prop.name}>
              {prop.displayName || prop.name}
            </label>
            <input
              id={prop.name}
              name={prop.name}
              type={prop.type === 'Boolean' || prop.type === 'bool' ? 'checkbox' : prop.type === 'number' ? 'number' : 'text'}
              className="border rounded px-2 py-1 w-full"
              value={
                prop.type === 'Boolean' || prop.type === 'bool'
                  ? undefined
                  : prop.type === 'number'
                    ? values[prop.name] ?? ''
                    : values[prop.name] ?? ''
              }
              checked={prop.type === 'Boolean' || prop.type === 'bool' ? !!values[prop.name] : undefined}
              onChange={e =>
                onChange(
                  prop.name,
                  prop.type === 'Boolean' || prop.type === 'bool'
                    ? e.currentTarget.checked
                    : prop.type === 'number'
                      ? e.currentTarget.value === '' ? '' : Number(e.currentTarget.value)
                      : e.currentTarget.value
                )
              }
            />
            {/* Optionally: Render validation info, help text, etc. */}
            {prop.attributes.includes('RequiredAttribute') && (
              <span className="text-xs text-red-500 ml-2">*</span>
            )}
          </div>
        ))}
        {error && <div className="text-red-600 mb-2">{error}</div>}
        <button
          type="submit"
          className="bg-blue-600 text-white px-4 py-2 rounded disabled:opacity-50"
          disabled={isSaving}
        >
          {isSaving ? 'Saving...' : 'Save'}
        </button>
      </form>
    </div>
  );
};
