import type { ToolheadDto } from '@/types/api';

/**
 * A physical, maintainable toolhead. The #711 backend contract is physical-only:
 * the server excludes MMU/AMS gate toolheads from every maintenance surface
 * (schedules, alerts, logs, statistics, odometers), so the UI mirrors that
 * exact rule. There is no client-side opt-in for gate toolheads.
 */
export type MaintenanceEligibleToolhead = ToolheadDto;

/**
 * Returns true when the toolhead can act as an independent hotend-maintenance
 * scope (per-toolhead schedules, alerts, logs, odometers).
 *
 * Rules (finalized against #711/#752 wire contract):
 * - Physical toolheads are eligible.
 * - MMU / AMS gate toolheads are excluded (server-enforced; this is the
 *   client-side mirror so we never construct a request the API would reject).
 * - Unknown / missing types are excluded.
 */
export function isEligibleMaintenanceToolhead(
  toolhead: ToolheadDto | null | undefined
): boolean {
  if (!toolhead) {
    return false;
  }
  return normalizeToolheadType(toolhead.toolheadType) === 'Physical';
}

/**
 * Filter helper that returns the eligible physical toolheads from a list,
 * sorted by index (falling back to name / id) for stable presentation.
 */
export function selectMaintenanceEligibleToolheads(
  toolheads: readonly ToolheadDto[] | null | undefined
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
