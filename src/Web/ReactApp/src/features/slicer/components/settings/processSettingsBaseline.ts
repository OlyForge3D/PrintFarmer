/**
 * Builds a COMPLETE, native-typed baseline for the metadata-driven process
 * settings editor.
 *
 * The editor renders ~305 keys from orcaSettingsMetadata.json, but an OrcaSlicer
 * process profile only declares a small subset explicitly (it relies on
 * `inherits:`). Previously the "original" snapshot was seeded only from those
 * explicit keys, so the change-tracker (which requires `origVal !== undefined`)
 * never showed a reset button for the ~250 keys that fell back to a metadata
 * default. This resolver fills in a default for every metadata key so every
 * editable field has a baseline to diff against.
 *
 * Values are coerced to the SAME native types the editor writes on change
 * (numbers as numbers, booleans as booleans, everything else as strings) so a
 * re-entered value compares equal instead of triggering a false "modified" flag.
 */
import metadata from '@/features/slicer/generated/orcaSettingsMetadata.json';
import {
  resolveControlType,
  toNumber,
  toBool,
  toString,
  type SettingMetadata,
  type ProfileTypeMetadata,
} from '@/features/slicer/components/settings/metadataTypes';

/** Coerce a raw value (or the metadata default when raw is undefined) to the editor's native type. */
export function coerceSettingValue(raw: unknown, meta: SettingMetadata): unknown {
  switch (resolveControlType(meta)) {
    case 'number':
      return toNumber(raw, meta);
    case 'checkbox':
      return toBool(raw, meta);
    // point / coFloats / text / select / color / textarea are stored as strings
    // by the editor's onUpdate, so keep them as strings here too.
    default:
      return toString(raw, meta);
  }
}

/**
 * Resolve the full baseline for the process editor: every metadata key present,
 * taking the profile's explicit value when given, otherwise the metadata default.
 */
export function resolveProcessSettingsBaseline(
  profileSettings: Record<string, unknown>,
): Record<string, unknown> {
  const processMeta = (metadata as unknown as Record<string, ProfileTypeMetadata>).process;
  const allSettings = processMeta.settings;
  const out: Record<string, unknown> = {};

  for (const [key, meta] of Object.entries(allSettings)) {
    // Skip developer-only settings — they are never rendered, so a baseline for
    // them is unnecessary noise.
    if (meta.mode === 'developer') continue;
    const raw = key in profileSettings ? profileSettings[key] : undefined;
    // Only seed keys that have either an explicit value or a usable default.
    if (raw === undefined && meta.default === undefined) continue;
    out[key] = coerceSettingValue(raw, meta);
  }

  return out;
}
