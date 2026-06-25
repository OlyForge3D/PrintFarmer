/**
 * Pure mapping functions between the Simple-mode SlicerSettings UI model
 * and OrcaProcessSettings (the raw OrcaSlicer parameter object).
 *
 * Extracted here so they can be unit-tested independently of the page component.
 */
import type { OrcaProcessSettings } from '@/features/slicer/components/settings/slicerSettingsTypes';
import type { SlicerSettings } from '@/features/slicer/components/job/SlicerSettingsPanel';

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
    supportType: typeof orca.support_type === 'string' ? orca.support_type : 'normal(auto)',
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
  return {
    ...prev,
    sparse_infill_density: settings.infillPercent,
    sparse_infill_pattern: settings.infillPattern,
    top_shell_layers: settings.topShellLayers,
    bottom_shell_layers: settings.bottomShellLayers,
    wall_loops: settings.wallLoops,
    enable_support: settings.supportEnabled,
    support_type: settings.supportEnabled ? settings.supportType : prev.support_type,
    brim_type: settings.bedAdhesionType === 'brim' ? 'outer_only' : 'no_brim',
    skirt_loops: settings.bedAdhesionType === 'skirt' ? 1 : 0,
    raft_layers: settings.bedAdhesionType === 'raft' ? 3 : 0,
  };
}
