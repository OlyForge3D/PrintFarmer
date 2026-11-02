import { getMetadataForVersion } from '@/features/slicer/components/settings/orcaSettingsMetadataResolver';
import type { SettingMetadata } from '@/features/slicer/components/settings/metadataTypes';

const filamentMetadata = getMetadataForVersion(undefined).profileTypes.filament.settings;
const vectorTypes = new Set([
  'coFloats', 'coInts', 'coBools', 'coStrings', 'coPercents', 'coEnums',
]);

export function hydrateFilamentProfileSettings(
  rawSettings: Record<string, unknown>,
): Record<string, unknown> {
  const settings = { ...rawSettings };
  if (!Object.hasOwn(settings, 'chamber_temperature')
    && Object.hasOwn(settings, 'chamber_temperatures')) {
    settings.chamber_temperature = settings.chamber_temperatures;
  }
  delete settings.chamber_temperatures;
  for (const [key, value] of Object.entries(settings)) {
    const meta = filamentMetadata[key];
    if (meta && vectorTypes.has(meta.coType) && Array.isArray(value)) {
      // Only coFloats has a multi-extruder control; other controls edit entry one.
      settings[key] = meta.coType === 'coFloats' ? value.join(',') : value[0];
    }
  }
  return settings;
}

function serializeEntry(value: unknown, meta: SettingMetadata): string {
  if (meta.coType === 'coBools') {
    if (value === true || value === '1' || value === 'true') return '1';
    if (value === false || value === '0' || value === 'false') return '0';
    throw new Error(`${meta.label} must be true or false.`);
  }
  if (typeof value !== 'string' && typeof value !== 'number') {
    throw new Error(`${meta.label} requires a text or numeric value.`);
  }
  if (['coFloats', 'coInts', 'coPercents'].includes(meta.coType)) {
    const text = String(value).trim();
    const number = Number(meta.coType === 'coPercents' ? text.replace(/%$/, '') : text);
    if (!text || !Number.isFinite(number)
      || (meta.coType === 'coInts' && !Number.isInteger(number))) {
      throw new Error(`${meta.label} requires a valid ${meta.coType === 'coInts' ? 'integer' : 'number'}.`);
    }
    return meta.coType === 'coPercents' ? `${number}%` : String(number);
  }
  return String(value);
}

export function serializeFilamentProfileSettings(
  settings: Record<string, unknown>,
  originalSettings: Record<string, unknown>,
  rawSettings: Record<string, unknown>,
): Record<string, unknown> {
  const serialized = { ...rawSettings };
  for (const key of new Set([...Object.keys(settings), ...Object.keys(originalSettings)])) {
    const hasCurrent = Object.hasOwn(settings, key);
    if (hasCurrent === Object.hasOwn(originalSettings, key)
      && JSON.stringify(settings[key]) === JSON.stringify(originalSettings[key])) {
      continue;
    }
    // Orca loads the legacy alias into the same option and can overwrite an edit.
    if (key === 'chamber_temperature') {
      delete serialized.chamber_temperatures;
    }
    if (!hasCurrent) {
      delete serialized[key];
      continue;
    }
    const meta = filamentMetadata[key];
    if (!meta || !vectorTypes.has(meta.coType)) {
      serialized[key] = settings[key];
      continue;
    }
    if (meta.coType === 'coFloats') {
      const entries = Array.isArray(settings[key]) ? settings[key] : String(settings[key]).split(',');
      serialized[key] = entries.map((entry) => serializeEntry(entry, meta));
    } else {
      const raw = key === 'chamber_temperature' && !Object.hasOwn(rawSettings, key)
        ? rawSettings.chamber_temperatures
        : rawSettings[key];
      serialized[key] = [
        serializeEntry(settings[key], meta),
        ...(Array.isArray(raw) ? raw.slice(1) : []),
      ];
    }
  }
  return serialized;
}
