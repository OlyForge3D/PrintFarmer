import type { LoadoutSlot } from '@/features/printers/utils/materialLoadout';
import type { SpoolmanSpool } from '@/types/api';

export type FilamentFeedState = 'loaded' | 'not-loaded' | 'available' | 'unknown';

/** Assignment, Spoolman quantity and physical feed state are independent facts. */
export function materialSlotState(slot: LoadoutSlot, spool: SpoolmanSpool | undefined, feed: FilamentFeedState) {
  const assigned = slot.spoolId != null && slot.spoolId > 0;
  const matched = assigned && spool != null && spool.id === slot.spoolId;
  const weight = !assigned ? null : matched ? spool.remainingWeightG : slot.remainingWeightG;
  const knownWeight = weight != null && Number.isFinite(weight) && weight >= 0 ? weight : null;
  // Only a matching Spoolman record or the printer-level spool snapshot can confirm depletion — never coverage estimates.
  const depleted = knownWeight === 0;
  const assignment = !assigned ? 'No spool assigned'
    : depleted ? 'Assigned spool depleted' : `Spool #${slot.spoolId}`;
  const quantity = !assigned ? undefined : knownWeight == null ? 'Remaining unknown'
    : `${knownWeight > 0 && knownWeight < 1 ? '<1' : Math.round(knownWeight)}g remaining`;
  const filament = feed === 'loaded' ? 'Filament loaded'
    : feed === 'not-loaded' ? 'Filament not loaded'
    : feed === 'available' ? 'Filament available' : 'Loading state unknown';
  return { assigned, depleted, assignment, quantity, filament };
}
