import { describe, it, expect } from 'vitest';
import {
  isEligibleMaintenanceToolhead,
  selectMaintenanceEligibleToolheads,
  type MaintenanceEligibleToolhead,
} from '../isEligibleMaintenanceToolhead';
import type { ToolheadDto } from '@/types/api';

function makeToolhead(
  overrides: Partial<ToolheadDto> & Pick<ToolheadDto, 'id'>
): MaintenanceEligibleToolhead {
  return {
    id: overrides.id,
    index: overrides.index ?? 0,
    isPrimary: overrides.isPrimary ?? overrides.index === 0,
    toolheadType: overrides.toolheadType ?? 'Physical',
    name: overrides.name,
  };
}

describe('isEligibleMaintenanceToolhead', () => {
  it('returns false for null/undefined', () => {
    expect(isEligibleMaintenanceToolhead(null)).toBe(false);
    expect(isEligibleMaintenanceToolhead(undefined)).toBe(false);
  });

  it('is true for physical toolheads', () => {
    expect(isEligibleMaintenanceToolhead(makeToolhead({ id: 't-0', toolheadType: 'Physical' }))).toBe(true);
  });

  it('excludes MMU/AMS gate toolheads (#711/#719 physical-only contract)', () => {
    expect(isEligibleMaintenanceToolhead(makeToolhead({ id: 'g-0', toolheadType: 'MmuGate' }))).toBe(false);
  });

  it('normalizes numeric ToolheadType (0=Physical, 1=MmuGate)', () => {
    expect(isEligibleMaintenanceToolhead(makeToolhead({ id: 't', toolheadType: 0 as unknown as string }))).toBe(true);
    expect(isEligibleMaintenanceToolhead(makeToolhead({ id: 'g', toolheadType: 1 as unknown as string }))).toBe(false);
  });

  it('excludes unknown toolhead types', () => {
    expect(
      isEligibleMaintenanceToolhead(
        makeToolhead({ id: 't', toolheadType: 'MysteryLoop' as unknown as string })
      )
    ).toBe(false);
  });

  it('never opts in via a rogue extra field (physical-only is server-enforced)', () => {
    // Even if some future/rogue backend field appears on the wire, the client
    // must not use it to bypass the physical-only rule. Only `toolheadType`
    // is authoritative.
    const rogue = {
      ...makeToolhead({ id: 'g', toolheadType: 'MmuGate' }),
      supportsMaintenanceScope: true,
    } as unknown as ToolheadDto;
    expect(isEligibleMaintenanceToolhead(rogue)).toBe(false);
  });
});

describe('selectMaintenanceEligibleToolheads', () => {
  it('returns an empty list for missing / empty input', () => {
    expect(selectMaintenanceEligibleToolheads(undefined)).toEqual([]);
    expect(selectMaintenanceEligibleToolheads(null)).toEqual([]);
    expect(selectMaintenanceEligibleToolheads([])).toEqual([]);
  });

  it('filters out MMU gates and preserves physical toolheads', () => {
    const toolheads = [
      makeToolhead({ id: 't-0', index: 0, toolheadType: 'Physical' }),
      makeToolhead({ id: 'g-0', index: 1, toolheadType: 'MmuGate' }),
      makeToolhead({ id: 't-1', index: 2, toolheadType: 'Physical' }),
    ];
    const result = selectMaintenanceEligibleToolheads(toolheads);
    expect(result.map(t => t.id)).toEqual(['t-0', 't-1']);
  });

  it('sorts eligible toolheads by index, then name, then id', () => {
    const toolheads = [
      makeToolhead({ id: 't-b', index: 2, name: 'B' }),
      makeToolhead({ id: 't-a', index: 2, name: 'A' }),
      makeToolhead({ id: 't-c', index: 0, name: 'C' }),
    ];
    expect(selectMaintenanceEligibleToolheads(toolheads).map(t => t.id)).toEqual(['t-c', 't-a', 't-b']);
  });

  it('does not mutate the input list', () => {
    const toolheads = [
      makeToolhead({ id: 't-2', index: 2 }),
      makeToolhead({ id: 't-0', index: 0 }),
    ];
    const before = toolheads.map(t => t.id);
    selectMaintenanceEligibleToolheads(toolheads);
    expect(toolheads.map(t => t.id)).toEqual(before);
  });
});
