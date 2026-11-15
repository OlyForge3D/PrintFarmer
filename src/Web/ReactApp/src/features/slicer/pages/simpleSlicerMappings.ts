/**
 * Pure mapping functions between the Simple-mode SlicerSettings UI model
 * and OrcaProcessSettings (the raw OrcaSlicer parameter object).
 *
 * Extracted here so they can be unit-tested independently of the page component.
 */
import type { OrcaProcessSettings } from '@/features/slicer/components/settings/slicerSettingsTypes';
import type { SlicerSettings } from '@/features/slicer/components/job/SlicerSettingsPanel';
import type { OrcaProcessProfile } from '@/services/slicerProfilesService';
import { resolveProcessSettingsBaseline } from '@/features/slicer/components/settings/processSettingsBaseline';

type AdhesionFeature = 'raft' | 'skirt' | 'brim';

/** The effective bed adhesion of a process profile, as far as it can be known. */
export type ProfileAdhesion =
  | { kind: 'single'; type: 'none' | AdhesionFeature }
  | { kind: 'compound'; features: AdhesionFeature[] }
  | { kind: 'unknown' };

/**
 * Classifies a profile's bed adhesion from its effective `raft_layers`,
 * `skirt_loops` and `brim_type`.
 *
 * Worker library profiles arrive with their `inherits:` chain already merged,
 * so a key that is still missing was not declared anywhere we could resolve
 * (OrcaSlicer then applies its built-in default, or the parent was never
 * found). Such a key is unknown: it is NOT read as "off" and not guessed from
 * metadata defaults, so the profile is reported as `unknown` rather than
 * inventing a None or a combination.
 */
export function describeProfileAdhesion(orca: OrcaProcessSettings): ProfileAdhesion {
  const { raft_layers: raft, skirt_loops: skirt, brim_type: brim } = orca;
  if (typeof raft !== 'number' || typeof skirt !== 'number' || typeof brim !== 'string' || brim === '') {
    return { kind: 'unknown' };
  }
  const features: AdhesionFeature[] = [];
  if (raft > 0) features.push('raft');
  if (skirt > 0) features.push('skirt');
  if (brim !== 'no_brim') features.push('brim');
  if (features.length > 1) return { kind: 'compound', features };
  return { kind: 'single', type: features[0] ?? 'none' };
}

function profileAdhesionLabel(adhesion: ProfileAdhesion): string {
  return adhesion.kind === 'compound'
    ? `Profile: ${adhesion.features.join(' + ')}`
    : 'Profile-controlled (inherited value unknown)';
}

/**
 * Derives the Simple-mode UI settings from a raw OrcaProcessSettings object.
 *
 * Bed adhesion: a profile with exactly one adhesion feature (or explicitly
 * none) maps to that choice. A combination (e.g. skirt + brim) or an
 * unresolved inherited value maps to `profile`, labelled with what is known,
 * so the panel never names a single choice the slicer would not produce.
 */
export function orcaToSimpleSettings(orca: OrcaProcessSettings): SlicerSettings {
  const adhesion = describeProfileAdhesion(orca);
  const bedAdhesionType: SlicerSettings['bedAdhesionType'] =
    adhesion.kind === 'single' ? adhesion.type : 'profile';

  return {
    ...(bedAdhesionType === 'profile' ? { bedAdhesionProfileLabel: profileAdhesionLabel(adhesion) } : {}),
    layerHeight: typeof orca.layer_height === 'number' ? orca.layer_height : 0.2,
    infillPercent: typeof orca.sparse_infill_density === 'number' ? orca.sparse_infill_density : 15,
    infillPattern: typeof orca.sparse_infill_pattern === 'string' ? orca.sparse_infill_pattern : 'grid',
    topShellLayers: typeof orca.top_shell_layers === 'number' ? orca.top_shell_layers : 4,
    bottomShellLayers: typeof orca.bottom_shell_layers === 'number' ? orca.bottom_shell_layers : 3,
    wallLoops: typeof orca.wall_loops === 'number' ? orca.wall_loops : 2,
    supportEnabled: orca.enable_support === true,
    supportType:
      typeof orca.support_type === 'string' && orca.support_type.startsWith('tree')
        ? 'tree(auto)'
        : 'normal(auto)',
    bedAdhesionType,
  };
}

/**
 * Merges Simple-mode UI settings back into an OrcaProcessSettings object.
 *
 * Bed adhesion fan-out:
 *   brim  → brim_type: 'outer_only', skirt_loops: 0, raft_layers: 0
 *   skirt → brim_type: 'no_brim',   skirt_loops: 1, raft_layers: 0
 *   raft  → brim_type: 'no_brim',   skirt_loops: 0, raft_layers: 3
 *   none  → brim_type: 'no_brim',   skirt_loops: 0, raft_layers: 0
 *
 * Bed adhesion `profile` (a compound or unresolved profile state) keeps the
 * previous adhesion keys exactly as they were, including absent ones, so the
 * profile's own semantics are sliced until the user picks an explicit choice.
 *
 * support_type is preserved from prev when supports are disabled.
 * layer_height is intentionally omitted: governed by the process-profile preset in Simple mode.
 */
export function simpleToOrcaSettings(
  settings: SlicerSettings,
  prev: OrcaProcessSettings
): OrcaProcessSettings {
  const adhesion =
    settings.bedAdhesionType === 'profile'
      ? { brim_type: prev.brim_type, skirt_loops: prev.skirt_loops, raft_layers: prev.raft_layers }
      : {
          brim_type: settings.bedAdhesionType === 'brim' ? 'outer_only' : 'no_brim',
          skirt_loops: settings.bedAdhesionType === 'skirt' ? 1 : 0,
          raft_layers: settings.bedAdhesionType === 'raft' ? 3 : 0,
        };
  return {
    ...prev,
    sparse_infill_density: settings.infillPercent,
    sparse_infill_pattern: settings.infillPattern,
    top_shell_layers: settings.topShellLayers,
    bottom_shell_layers: settings.bottomShellLayers,
    wall_loops: settings.wallLoops,
    enable_support: settings.supportEnabled,
    support_type: settings.supportEnabled ? settings.supportType : prev.support_type,
    ...adhesion,
  };
}

/**
 * Resolves the explicit bed-adhesion keys to send with a Simple-mode slice job so
 * the slicer produces exactly the adhesion the Simple panel displays.
 *
 * A single displayed choice (including None) is always sent as all three keys,
 * so OrcaSlicer cannot fall back to its own default (`auto_brim`) and print a
 * brim the user never saw.
 *
 * A `profile` state (a combination such as skirt + brim, or an inherited value
 * that could not be resolved) returns no forced keys: the profile's own
 * adhesion is sliced, which is what the panel says. Once the user picks an
 * explicit choice, `simpleToOrcaSettings` has written known values, and those
 * are sent.
 */
export function resolveAdhesionOverrides(
  settings: OrcaProcessSettings,
): Partial<Pick<OrcaProcessSettings, 'brim_type' | 'skirt_loops' | 'raft_layers'>> {
  const adhesion = describeProfileAdhesion(settings);
  if (adhesion.kind !== 'single') return {};
  switch (adhesion.type) {
    case 'brim':
      return {
        brim_type:
          typeof settings.brim_type === 'string' && settings.brim_type !== 'no_brim'
            ? settings.brim_type
            : 'outer_only',
        skirt_loops: 0,
        raft_layers: 0,
      };
    case 'skirt':
      return { brim_type: 'no_brim', skirt_loops: settings.skirt_loops, raft_layers: 0 };
    case 'raft':
      return { brim_type: 'no_brim', skirt_loops: 0, raft_layers: settings.raft_layers };
    default:
      return { brim_type: 'no_brim', skirt_loops: 0, raft_layers: 0 };
  }
}

/**
 * Helper function to convert OrcaProcessProfile to OrcaProcessSettings
 * Maps profile data to settings structure, using defaults for missing values
 */
export function convertOrcaProcessProfileToSettings(profile: OrcaProcessProfile | undefined): OrcaProcessSettings {
  if (!profile) return {} as OrcaProcessSettings;

  // Parse settings from profile if available
  const profileSettings = (profile.settings ?? {}) as Record<string, unknown>;

  // Merge the four promoted fields and the raw profile JSON, then resolve a
  // COMPLETE baseline: every metadata-known key gets a value (the profile's
  // when present, otherwise its metadata default), coerced to the editor's
  // native type. This ensures every editable field has an original value so
  // the reset/modified affordance works for all settings, not just the ~50 a
  // profile declares explicitly. Raw settings win over the promoted fields:
  // those are server-derived summaries (with fallbacks) of the same keys, and
  // the raw values are what an untouched submission sends to the slicer.
  const merged: Record<string, unknown> = {
    ...(profile.layerHeight !== undefined ? { layer_height: profile.layerHeight } : {}),
    ...(profile.infillPercentage !== undefined ? { sparse_infill_density: profile.infillPercentage } : {}),
    ...(profile.printSpeed !== undefined ? { outer_wall_speed: profile.printSpeed } : {}),
    ...(profile.supports !== undefined ? { enable_support: profile.supports } : {}),
    ...profileSettings,
  };

  const resolved = resolveProcessSettingsBaseline(merged);
  // A metadata default is not evidence of what the profile slices with, so
  // adhesion keys the profile never declared stay absent (see describeProfileAdhesion).
  for (const key of ADHESION_KEYS) {
    if (!(key in merged)) delete resolved[key];
  }
  return resolved as unknown as OrcaProcessSettings;
}

const ADHESION_KEYS = ['raft_layers', 'skirt_loops', 'brim_type'] as const;
