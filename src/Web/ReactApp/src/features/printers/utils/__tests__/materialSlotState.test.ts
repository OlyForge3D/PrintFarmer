import { describe, expect, it } from 'vitest';
import { materialSlotState } from '@/features/printers/utils/materialSlotState';
import type { LoadoutSlot } from '@/features/printers/utils/materialLoadout';
import type { SpoolmanSpool } from '@/types/api';

const slot: LoadoutSlot = { key: 'tool-0', apiIndex: 0, label: 'T0', source: 'tool', spoolId: 7 };
function spool(weight: number | null | undefined, id = 7): SpoolmanSpool {
  return { id, name: 'PLA', material: 'PLA', inUse: false, remainingWeightG: weight };
}

describe('independent material facts', () => {
  it.each([null, undefined, -1, NaN, Infinity])('does not claim depletion for invalid/unknown quantity %s', (weight) => {
    expect(materialSlotState(slot, spool(weight), 'not-loaded')).toMatchObject({ depleted: false, quantity: 'Remaining unknown', filament: 'Filament not loaded' });
  });
  it('requires a matching inventory identity to confirm zero', () => {
    expect(materialSlotState(slot, spool(0, 8), 'loaded').depleted).toBe(false);
    expect(materialSlotState(slot, spool(0), 'loaded')).toMatchObject({ depleted: true, filament: 'Filament loaded', assignment: 'Assigned spool depleted' });
  });
  it('does not infer assignment from physically loaded material', () => {
    expect(materialSlotState({ ...slot, spoolId: undefined }, spool(0), 'loaded')).toMatchObject({ assigned: false, depleted: false, assignment: 'No spool assigned', filament: 'Filament loaded' });
  });
  it('does not round a positive amount into a depleted-looking zero', () => {
    expect(materialSlotState(slot, spool(0.2), 'unknown')).toMatchObject({ depleted: false, quantity: '<1g remaining' });
  });
});
