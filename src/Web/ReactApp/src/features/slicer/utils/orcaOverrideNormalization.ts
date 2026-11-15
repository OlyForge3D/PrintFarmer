/**
 * Builds the process-setting overrides sent with a slice job and normalises
 * them to OrcaSlicer's native config representation.
 *
 * OrcaSlicer profile JSON stores every value as a string: percents carry a
 * trailing `%` ("15%"), booleans are "1"/"0", numbers are plain decimal text.
 * The editor keeps native JS types (15, true), and raw profile values arrive
 * as strings, so the merged overrides mix both. Normalising here gives the
 * worker one consistent, Orca-native shape regardless of where a value came from.
 */
import metadata from '@/features/slicer/generated/orcaSettingsMetadata.json';
import type { ProfileTypeMetadata } from '@/features/slicer/components/settings/metadataTypes';
import { scrubSettingsForVersion } from '@/features/slicer/components/settings/orcaSettingsMetadataResolver';
import type { OrcaProcessSettings } from '@/features/slicer/components/settings/slicerSettingsTypes';
import { resolveAdhesionOverrides } from '@/features/slicer/pages/simpleSlicerMappings';
import { diffProcessOverrides } from '@/features/slicer/utils/slicePayload';

const processSettingTypes: Record<string, string | undefined> = Object.fromEntries(
  Object.entries((metadata as unknown as Record<string, ProfileTypeMetadata>).process.settings).map(
    ([key, meta]) => [key, meta.type],
  ),
);

const NUMERIC_TEXT = /^-?\d+(\.\d+)?$/;

function normalizeValue(value: unknown, type: string | undefined): unknown {
  if (typeof value === 'boolean') return value ? '1' : '0';

  if (type === 'bool' && typeof value === 'string') {
    if (value === 'true') return '1';
    if (value === 'false') return '0';
    return value;
  }

  if (type === 'percent') {
    if (typeof value === 'number' && Number.isFinite(value)) return `${value}%`;
    if (typeof value === 'string' && NUMERIC_TEXT.test(value.trim())) return `${value.trim()}%`;
    return value;
  }

  if (typeof value === 'number' && Number.isFinite(value)) return String(value);

  return value;
}

/** Normalise process overrides to OrcaSlicer's native string representation. */
export function normalizeOrcaOverrides(overrides: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(overrides)) {
    out[key] = normalizeValue(value, processSettingTypes[key]);
  }
  return out;
}

export interface BuildProcessOverridesInput {
  /** Current editor state (native-typed). */
  slicerSettings: OrcaProcessSettings;
  /** Baseline snapshot the editor was seeded from. */
  originalProcessSettings: Record<string, unknown>;
  /** Raw settings applied verbatim from the Advanced editor's profile. */
  advancedProcessSettings: Record<string, unknown>;
  /** True when the Simple slicer panel is active. */
  simpleMode: boolean;
  engineVersion: string | null | undefined;
}

/**
 * Compute the overrides payload for a slice job: the user's edits on top of the
 * raw advanced settings, with Simple-mode adhesion pinned to the displayed
 * choice, scrubbed for the target engine version and normalised.
 */
export function buildProcessOverrides({
  slicerSettings,
  originalProcessSettings,
  advancedProcessSettings,
  simpleMode,
  engineVersion,
}: BuildProcessOverridesInput): Record<string, unknown> {
  const modified = diffProcessOverrides(
    slicerSettings as unknown as Record<string, unknown>,
    originalProcessSettings,
  );
  const merged: Record<string, unknown> = {
    ...advancedProcessSettings,
    ...modified,
    ...(simpleMode ? resolveAdhesionOverrides(slicerSettings) : {}),
  };
  return normalizeOrcaOverrides(scrubSettingsForVersion(merged, 'process', engineVersion));
}
