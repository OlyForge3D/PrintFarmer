/**
 * Version-aware OrcaSlicer settings metadata resolver (issue #578).
 *
 * The upstream `orcaSettingsMetadata.json` bundle is extracted from a single
 * OrcaSlicer C++ checkout (currently 2.4.x). This resolver derives a per-
 * version view of that bundle so callers can render the correct field set
 * for the engine version a slice job is pinned to:
 *
 *   getMetadataForVersion('2.4.1')  → full 2.4 bundle (unchanged)
 *   getMetadataForVersion('2.3.1')  → 2.4 bundle minus 2.4-added fields,
 *                                     with 2.4-era renames reversed
 *   getMetadataForVersion(undefined) → full bundle (engine-agnostic pages)
 *
 * The delta comes from `orca-settings-version-delta.ts` (hand-maintained,
 * one source of truth). The resolver itself is pure — no I/O, no queries —
 * and safe to call every render (React Query cache-keys by version anyway).
 */
import metadata from '@/features/slicer/generated/orcaSettingsMetadata.json';
import {
  getOrcaVersionDeltas,
  versionMeets,
} from '@/features/slicer/generated/orca-settings-version-delta';
import type { ProfileTypeMetadata, ProfileType, SettingMetadata, TabLayout } from './metadataTypes';

export interface VersionScopedMetadata {
  /** The rendered profile-type metadata (tabs + settings) for the target version. */
  profileTypes: Record<ProfileType, ProfileTypeMetadata>;
  /**
   * Rename mapping applied for this version.
   * Key = the key USED IN THIS VERSION.
   * Value = the key used in NEWER versions (the "canonical" post-rename key).
   *
   * A caller migrating in-flight form state can look up
   * `renameFromNewToThis[newKey]` to translate a payload from a newer engine
   * to this engine's key set.
   */
  renameFromNewToThis: Record<string, string>;
  renameFromThisToNew: Record<string, string>;
  /** The resolved version tag, or 'unfiltered' if no version was supplied. */
  resolvedFor: string;
}

type MetadataBundle = {
  filament: ProfileTypeMetadata;
  machine: ProfileTypeMetadata;
  process: ProfileTypeMetadata;
};

const RAW_METADATA = metadata as unknown as MetadataBundle & { _meta?: unknown; icons?: unknown };

function cloneTab(tab: TabLayout, keptKeys: Set<string>): TabLayout {
  return {
    ...tab,
    sections: tab.sections
      .map((s) => ({
        ...s,
        fields: s.fields.filter((f) => keptKeys.has(f.key)),
      }))
      .filter((s) => s.fields.length > 0),
  };
}

function cloneProfileTypeMetadata(
  src: ProfileTypeMetadata,
  keptKeys: Set<string>,
  renameFromThisToNew: Record<string, string>,
): ProfileTypeMetadata {
  const settings: Record<string, SettingMetadata> = {};
  for (const [k, v] of Object.entries(src.settings)) {
    if (!keptKeys.has(k)) continue;
    settings[k] = v;
  }
  // Apply rename-in-this-version: emit renamed field under the older key.
  for (const [thisKey, newKey] of Object.entries(renameFromThisToNew)) {
    const m = src.settings[newKey];
    if (!m) continue;
    settings[thisKey] = { ...m, key: thisKey };
    delete settings[newKey];
  }
  const tabs = src.tabs
    .map((t) => cloneTab(t, new Set(Object.keys(settings))))
    .filter((t) => t.sections.length > 0);
  return { tabs, settings };
}

const PROFILE_TYPES: ProfileType[] = ['filament', 'machine', 'process'];

/**
 * Compute the version-scoped metadata bundle for a target engine version.
 * Pure function — same inputs → same outputs.
 */
export function getMetadataForVersion(engineVersion: string | null | undefined): VersionScopedMetadata {
  return computeVersionScopedMetadata(engineVersion, getOrcaVersionDeltas());
}

/**
 * Pure core of {@link getMetadataForVersion}. Exposed so tests can inject a
 * synthetic delta list to prove the added/removed/renamed mechanics without
 * mutating the production delta table.
 */
export function computeVersionScopedMetadata(
  engineVersion: string | null | undefined,
  deltas: { minVersion: string; delta: import('@/features/slicer/generated/orca-settings-version-delta').OrcaVersionDelta }[],
): VersionScopedMetadata {
  // Which future-version additions are HIDDEN because they didn't exist yet.
  const hidden = new Set<string>();
  // Which renames apply BACKWARDS for this version.
  //   renameFromThisToNew['firstLayerAdhesion'] = 'bedAdhesionOverride'
  //   means the field lives under `firstLayerAdhesion` in this version and
  //   `bedAdhesionOverride` in the newer version.
  const renameFromThisToNew: Record<string, string> = {};

  // No version specified → treat as "engine-agnostic": full union bundle,
  // no filtering, no renames. This is safe for profile management pages
  // that don't bind to a specific engine version.
  const shouldFilter = engineVersion !== null && engineVersion !== undefined && engineVersion !== '';

  if (shouldFilter) {
    for (const { minVersion, delta } of deltas) {
      if (!versionMeets(engineVersion, minVersion)) {
        // The target engine is older than this delta — apply it backwards.
        for (const k of delta.addedIn) hidden.add(k);
        for (const [newKey, oldKey] of Object.entries(delta.renamedIn)) {
          // In the target version, the OLD key is what's present.
          renameFromThisToNew[oldKey] = newKey;
          // And the NEW key must be hidden.
          hidden.add(newKey);
        }
      }
    }
  }

  const renameFromNewToThis: Record<string, string> = {};
  for (const [thisKey, newKey] of Object.entries(renameFromThisToNew)) {
    renameFromNewToThis[newKey] = thisKey;
  }

  const profileTypes = {} as Record<ProfileType, ProfileTypeMetadata>;
  for (const pt of PROFILE_TYPES) {
    const src = RAW_METADATA[pt];
    if (!src) continue;
    const keptKeys = new Set(
      Object.keys(src.settings).filter((k) => !hidden.has(k)),
    );
    profileTypes[pt] = cloneProfileTypeMetadata(src, keptKeys, renameFromThisToNew);
  }

  return {
    profileTypes,
    renameFromNewToThis,
    renameFromThisToNew,
    resolvedFor: engineVersion ?? 'unfiltered',
  };
}

/**
 * Scrub a settings-value dictionary to only include keys valid for the
 * target engine version, applying rename migrations in both directions.
 *
 * Used when the user switches the pinned engine version on a slice job so
 * `advancedProcessSettings` never carries stale keys from the previously
 * selected engine.
 *
 * @param settings   In-flight advanced settings state.
 * @param profileType Which profile type these settings belong to.
 * @param engineVersion The engine version now selected.
 * @returns A new dictionary. Original is not mutated.
 */
export function scrubSettingsForVersion(
  settings: Record<string, unknown>,
  profileType: ProfileType,
  engineVersion: string | null | undefined,
  deltasOverride?: { minVersion: string; delta: import('@/features/slicer/generated/orca-settings-version-delta').OrcaVersionDelta }[],
): Record<string, unknown> {
  const scoped = deltasOverride
    ? computeVersionScopedMetadata(engineVersion, deltasOverride)
    : getMetadataForVersion(engineVersion);
  const valid = scoped.profileTypes[profileType]?.settings ?? {};
  const validKeys = new Set(Object.keys(valid));
  const out: Record<string, unknown> = {};

  for (const [key, value] of Object.entries(settings)) {
    if (validKeys.has(key)) {
      // Already correct key for this version.
      out[key] = value;
      continue;
    }
    // If this key is the NEWER form of a renamed field and the target
    // version uses the older form, migrate the value.
    const migratedToOlder = scoped.renameFromNewToThis[key];
    if (migratedToOlder && validKeys.has(migratedToOlder)) {
      out[migratedToOlder] = value;
      continue;
    }
    // Otherwise the key is not valid for this version — drop it.
  }
  return out;
}
