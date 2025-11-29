import { useEffect, useState, useCallback } from 'react';
import { useLocation } from 'react-router-dom';
import { SettingsPagelet, SettingMetadata, SettingValue } from '../components/SettingsPagelet';
import { SettingInputType } from '../types/SettingInputType';
import { PageTemplate } from '@/components/PageTemplate';
import { Settings } from 'lucide-react';
import { Button } from '@/components/ui';
import {
  fetchSettingsMetadata,
  saveAllSettings,
  fetchSettingsUnified,
} from '../services/settingsApi';

export function SettingsPage() {
  const [metadata, setMetadata] = useState<SettingMetadata[]>([]);
  const [settingsValues, setSettingsValues] = useState<Record<string, Record<string, unknown>>>({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [fieldErrorsBySection, setFieldErrorsBySection] = useState<Record<string, Record<string, string>>>({});

  const location = useLocation();

  const refetchSettingsValues = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const meta = await fetchSettingsMetadata();
      setMetadata(meta);
      const unified = await fetchSettingsUnified();
      const valueMap: Record<string, Record<string, unknown>> = {};
      for (const m of meta) {
        const sectionKey = m.key;
        valueMap[sectionKey] = (unified && typeof unified === 'object' && sectionKey in unified) ? (unified as Record<string, unknown>)[sectionKey] as Record<string, unknown> : {};
      }
      setSettingsValues(valueMap);
      setLoading(false);
    } catch (err) {
      setError('Failed to reload settings after save.');
      setLoading(false);
      console.error('Error in refetchSettingsValues:', err);
    }
  }, []);

  useEffect(() => {
    let mounted = true;
    setLoading(true);
    setError(null);
    fetchSettingsMetadata()
      .then(async (meta) => {
        if (!mounted) return;
        setMetadata(meta);
        await refetchSettingsValues();
        setLoading(false);
      })
      .catch(() => {
        setError('Failed to load settings metadata.');
        setLoading(false);
      });
    return () => { mounted = false; };
  }, [location, refetchSettingsValues]);

  const handleFieldChange = (className: string, field: string, value: SettingValue) => {
    setSettingsValues(prev => ({
      ...prev,
      [className]: {
        ...(prev[className] || {}),
        [field]: value,
      }
    }));

    const metaForSection = metadata.find(m => m.key === className);
    if (metaForSection) {
      const sectionValues = {
        ...(settingsValues[className] || {}),
        [field]: value,
      };
      const errs = validateSection(metaForSection, sectionValues);
      setFieldErrorsBySection(prev => ({ ...prev, [className]: errs }));
    }
  };

  const handleSave = async () => {
    setSaving(true);
    setSaveError(null);
    try {
      const allErrors: Record<string, Record<string, string>> = {};
      for (const metaItem of metadata) {
        const sectionKey = metaItem.key;
        const vals = settingsValues[sectionKey] || {};
        const errs = validateSection(metaItem, vals);
        if (Object.keys(errs).length > 0) allErrors[sectionKey] = errs;
      }
      if (Object.keys(allErrors).length > 0) {
        setFieldErrorsBySection(allErrors);
        setSaveError('Fix validation errors before saving.');
        setSaving(false);
        return;
      }

      const payload: Record<string, unknown> = {};
      for (const meta of metadata) {
        const sectionKey = meta.key;
        payload[sectionKey] = settingsValues[sectionKey];
      }
      await saveAllSettings(payload);
      await refetchSettingsValues();
    } catch (err) {
      const maybe = err as unknown;
      if (typeof maybe === 'object' && maybe !== null) {
        const maybeObj = maybe as Record<string, unknown>;
        const resp = maybeObj['response'];
        if (resp && typeof resp === 'object') {
          const data = (resp as Record<string, unknown>)['data'];
          if (data && typeof data === 'object') {
            const errorsObj = (data as Record<string, unknown>)['errors'] as Record<string, unknown> | undefined;
            if (errorsObj && typeof errorsObj === 'object') {
              const newFieldErrors: Record<string, Record<string, string>> = {};
              for (const [key, msg] of Object.entries(errorsObj)) {
                const parts = key.split('.');
                let section = parts.length > 1 ? parts[0] : undefined;
                const fieldName = parts.length > 1 ? parts.slice(1).join('.') : parts[0];
                if (!section) {
                  const found = metadata.find(m => m.properties.some(p => p.name === fieldName));
                  section = found?.key;
                }
                if (section) {
                  newFieldErrors[section] = newFieldErrors[section] || {};
                  newFieldErrors[section][fieldName] = String(msg ?? 'Invalid value');
                } else {
                  setSaveError(String(msg ?? 'Failed to save settings.'));
                }
              }
              setFieldErrorsBySection(prev => ({ ...prev, ...newFieldErrors }));
            }
            if ('message' in (data as Record<string, unknown>) && !saveError) {
              setSaveError(String((data as Record<string, unknown>)['message'] ?? ''));
            }
          }
        }
      }
      if (!saveError) {
        setSaveError('Failed to save settings.');
      }
    } finally {
      setSaving(false);
    }
  };

  const validateSection = (metaItem: SettingMetadata, valuesObj: Record<string, unknown>): Record<string, string> => {
    const errs: Record<string, string> = {};
    for (const prop of metaItem.properties) {
      const val = valuesObj[prop.name];
      if (prop.attributes.includes('RequiredAttribute')) {
        const empty = val === undefined || val === null || val === '' || (Array.isArray(val) && val.length === 0);
        if (empty) { errs[prop.name] = 'This field is required.'; continue; }
      }
      const isNumberType = prop.display?.inputType === SettingInputType.Number || ['Number', 'int', 'double'].includes(prop.type);
      if (isNumberType) {
        const num = typeof val === 'number' ? val : (typeof val === 'string' && val !== '' ? Number(val) : NaN);
        if (!isNaN(num)) {
          if (typeof prop.display?.minValue === 'number' && num < prop.display!.minValue!) errs[prop.name] = `Minimum is ${prop.display!.minValue}`;
          if (typeof prop.display?.maxValue === 'number' && num > prop.display!.maxValue!) errs[prop.name] = `Maximum is ${prop.display!.maxValue}`;
        }
      }
    }
    return errs;
  };

  return (
    <PageTemplate
      title="Settings"
      subtitle="Configure PrintFarmer application settings"
      icon={Settings}
      maxWidth="max-w-3xl"
    >
      {loading ? (
        <div className="text-center text-gray-500">Loading settings...</div>
      ) : error ? (
        <div className="text-center text-red-600">{error}</div>
      ) : (
        <form onSubmit={e => { e.preventDefault(); handleSave(); }}>
          {metadata.map((meta) => (
            <SettingsPagelet
              key={meta.key}
              metadata={meta}
              values={(settingsValues[meta.key] || {}) as Record<string, SettingValue>}
              onChange={(field, value) => handleFieldChange(meta.key, field, value)}
              fieldErrors={fieldErrorsBySection[meta.key]}
            />
          ))}
          {saveError && <div className="text-pf-error mb-2">{saveError}</div>}
          <Button
            type="submit"
            variant="primary"
            disabled={saving}
          >
            {saving ? 'Saving...' : 'Save All'}
          </Button>
        </form>
      )}
    </PageTemplate>
  );
}