import { describe, it, expect } from 'vitest';
import metadata from '@/features/slicer/generated/orcaSettingsMetadata.json';
import {
  resolveProcessSettingsBaseline,
  coerceSettingValue,
} from '../processSettingsBaseline';
import type { ProfileTypeMetadata, SettingMetadata } from '../metadataTypes';

const processMeta = (metadata as unknown as Record<string, ProfileTypeMetadata>).process;

describe('resolveProcessSettingsBaseline', () => {
  it('seeds a baseline for far more keys than a sparse profile declares', () => {
    const sparse = { layer_height: '0.2', sparse_infill_density: '15%' };
    const baseline = resolveProcessSettingsBaseline(sparse);

    // The editor renders hundreds of keys; the baseline must cover the vast
    // majority (every non-developer metadata key with a default), not just the 2
    // declared. This is the core of the reset-button fix.
    expect(Object.keys(baseline).length).toBeGreaterThan(100);
    expect(Object.keys(baseline).length).toBeGreaterThan(Object.keys(sparse).length + 100);
  });

  it('uses the profile value when present and the metadata default otherwise', () => {
    const numericKey = Object.entries(processMeta.settings).find(
      ([, m]) => (m.type === 'float' || m.type === 'int') && m.default !== undefined,
    )?.[0];
    expect(numericKey).toBeDefined();

    const withOverride = resolveProcessSettingsBaseline({ [numericKey!]: '3' });
    expect(withOverride[numericKey!]).toBe(3); // coerced to a native number

    const withoutOverride = resolveProcessSettingsBaseline({});
    const def = processMeta.settings[numericKey!].default;
    expect(withoutOverride[numericKey!]).toBe(Number(def));
  });

  it('coerces values to the editor native types (number/bool/string)', () => {
    const numberMeta: SettingMetadata = { key: 'k', type: 'float', coType: 'coFloat', label: 'k', default: '0.4' };
    expect(coerceSettingValue('0.4', numberMeta)).toBe(0.4);
    expect(typeof coerceSettingValue('0.4', numberMeta)).toBe('number');

    const boolMeta: SettingMetadata = { key: 'k', type: 'bool', coType: 'coBool', label: 'k', default: 'false' };
    expect(coerceSettingValue('true', boolMeta)).toBe(true);
    expect(typeof coerceSettingValue('true', boolMeta)).toBe('boolean');

    const strMeta: SettingMetadata = { key: 'k', type: 'string', coType: 'coString', label: 'k', default: 'x' };
    expect(coerceSettingValue('grid', strMeta)).toBe('grid');
    expect(typeof coerceSettingValue('grid', strMeta)).toBe('string');
  });

  it('produces a baseline that compares EQUAL to a re-entered native value (no false modified)', () => {
    // profile declares layer_height "0.2" (string in JSON); the editor reads/writes
    // it as a native number. After coercion the baseline must equal that native
    // number so JSON.stringify(original) === JSON.stringify(current) when unchanged.
    const baseline = resolveProcessSettingsBaseline({ layer_height: '0.2' });
    const editorNativeValue = 0.2; // what onUpdate stores
    expect(JSON.stringify(baseline.layer_height)).toBe(JSON.stringify(editorNativeValue));
  });
});
