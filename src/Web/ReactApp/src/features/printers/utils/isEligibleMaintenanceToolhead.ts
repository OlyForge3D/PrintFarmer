import type { ToolheadDto } from '@/types/api';

/**
 * Extended shape the backend may attach to a toolhead once the per-toolhead
 * maintenance contract (#711) lands. Kept optional so this compiles against
 * the current shipping `ToolheadDto` and only reacts if the API opts in.
 */
export interface MaintenanceEligibleToolhead extends ToolheadDto {
  /**
   * When the backend explicitly marks a toolhead as eligible for hotend
   * maintenance scoping we honour it. This is the only way an MMU/AMS gate
   * can become an eligible maintenance target (see #719 acceptance).
   */
  supportsMaintenanceScope?: boolean | null;
}

/**
 * Returns true when the toolhead can act as an independent hotend-maintenance
 * scope (per-toolhead schedules, alerts, logs, odometers).
 *
 * Rules (derived from #711 backend consensus + #719 acceptance):
 * - Physical toolheads are always eligible.
 * - MMU / AMS gate toolheads are excluded by default.
 * - The API may opt any toolhead in via `supportsMaintenanceScope === true`.
 *   Explicit `false` always wins, even for physical toolheads.
 */
export function isEligibleMaintenanceToolhead(
  toolhead: MaintenanceEligibleToolhead | ToolheadDto | null | undefined
): boolean {
  if (!toolhead) {
    return false;
  }
  const extended = toolhead as MaintenanceEligibleToolhead;
  if (extended.supportsMaintenanceScope === true) {
    return true;
  }
  if (extended.supportsMaintenanceScope === false) {
    return false;
  }
  return normalizeToolheadType(toolhead.toolheadType) === 'Physical';
}

/**
 * Filter helper that returns the eligible physical toolheads from a list,
 * sorted by index (falling back to name / id) for stable presentation.
 */
export function selectMaintenanceEligibleToolheads(
  toolheads: readonly (MaintenanceEligibleToolhead | ToolheadDto)[] | null | undefined
): MaintenanceEligibleToolhead[] {
  if (!toolheads?.length) {
    return [];
  }
  return toolheads
    .filter(isEligibleMaintenanceToolhead)
    .slice()
    .sort((a, b) => {
      const ai = a.index ?? Number.MAX_SAFE_INTEGER;
      const bi = b.index ?? Number.MAX_SAFE_INTEGER;
      if (ai !== bi) return ai - bi;
      const an = (a.name ?? a.id ?? '').toString();
      const bn = (b.name ?? b.id ?? '').toString();
      return an.localeCompare(bn);
    });
}

function normalizeToolheadType(value: unknown): 'Physical' | 'MmuGate' | 'Unknown' {
  if (typeof value === 'string') {
    if (value === 'Physical' || value === 'MmuGate') return value;
    if (value === '0') return 'Physical';
    if (value === '1') return 'MmuGate';
    return 'Unknown';
  }
  if (typeof value === 'number') {
    if (value === 0) return 'Physical';
    if (value === 1) return 'MmuGate';
  }
  return 'Unknown';
}
