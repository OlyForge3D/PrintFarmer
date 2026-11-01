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

/**
 * Derives the Simple-mode UI settings from a raw OrcaProcessSettings object.
 *
 * Bed adhesion priority: raft_layers > 0 → raft, skirt_loops > 0 → skirt,
 * any brim_type other than 'no_brim' → brim, otherwise none.
 */
export function orcaToSimpleSettings(orca: OrcaProcessSettings): SlicerSettings {
  let bedAdhesionType: SlicerSettings['bedAdhesionType'] = 'none';
  if (typeof orca.raft_layers === 'number' && orca.raft_layers > 0) {
    bedAdhesionType = 'raft';
  } else if (typeof orca.skirt_loops === 'number' && orca.skirt_loops > 0) {
    bedAdhesionType = 'skirt';
  } else if (orca.brim_type && orca.brim_type !== 'no_brim') {
    bedAdhesionType = 'brim';
  }

  return {
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
 * support_type is preserved from prev when supports are disabled.
 * layer_height is intentionally omitted: governed by the process-profile preset in Simple mode.
 */
export function simpleToOrcaSettings(
  settings: SlicerSettings,
  prev: OrcaProcessSettings
): OrcaProcessSettings {
  // A compound profile (e.g. skirt + brim) collapses to one Simple choice; keep
  // its adhesion keys intact until the user explicitly picks a different one.
  const preserveAdhesion =
    hasCompoundAdhesion(prev) && settings.bedAdhesionType === orcaToSimpleSettings(prev).bedAdhesionType;
  const adhesion = preserveAdhesion
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
 * True when more than one adhesion feature (raft, skirt, brim) is active. The
 * Simple panel can only display one of them, so such profiles must not be
 * rewritten to the single displayed choice unless the user changes adhesion.
 */
export function hasCompoundAdhesion(orca: OrcaProcessSettings): boolean {
  const active = [
    typeof orca.raft_layers === 'number' && orca.raft_layers > 0,
    typeof orca.skirt_loops === 'number' && orca.skirt_loops > 0,
    typeof orca.brim_type === 'string' && orca.brim_type !== 'no_brim',
  ].filter(Boolean).length;
  return active > 1;
}

/**
 * Resolves the explicit bed-adhesion keys to send with a Simple-mode slice job so
 * the slicer produces exactly the adhesion the Simple panel displays.
 *
 * The displayed choice is derived by `orcaToSimpleSettings`, which treats a
 * missing `brim_type` as "none". Profiles that inherit `brim_type` leave it
 * unset, so omitting it from the overrides lets OrcaSlicer fall back to its
 * own default (`auto_brim`) and print a brim the user never saw. Always sending
 * all three keys removes that mismatch.
 *
 * Compound profiles (e.g. skirt + brim) return no forced keys: the profile's
 * own combination is sliced unless the user explicitly changed adhesion, in
 * which case `simpleToOrcaSettings` has already collapsed it to one choice.
 */
export function resolveAdhesionOverrides(
  settings: OrcaProcessSettings,
): Partial<Pick<OrcaProcessSettings, 'brim_type' | 'skirt_loops' | 'raft_layers'>> {
  if (hasCompoundAdhesion(settings)) return {};
  const displayed = orcaToSimpleSettings(settings).bedAdhesionType;
  switch (displayed) {
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

  return resolveProcessSettingsBaseline(merged) as unknown as OrcaProcessSettings;
}
