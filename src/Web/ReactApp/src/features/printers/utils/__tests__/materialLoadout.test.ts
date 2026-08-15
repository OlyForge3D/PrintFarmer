import { describe, it, expect } from 'vitest';
import { resolveMaterialLoadout, isLightColor } from '@/features/printers/utils/materialLoadout';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';
import type { MmuGate, MmuStatus, ToolheadDto } from '@/types/api';

function gate(index: number, overrides: Partial<MmuGate> = {}): MmuGate {
  return {
    index,
    status: 'Available',
    material: 'PLA',
    color: '#ff0000',
    spoolId: 0,
    ...overrides,
  } as MmuGate;
}

function mmu(gates: MmuGate[], mmuType?: string): MmuStatus {
  return { enabled: true, mmuType, numGates: gates.length, gates } as MmuStatus;
}

function toolhead(index: number, overrides: Partial<ToolheadDto> = {}): ToolheadDto {
  return {
    id: `th-${index}`,
    index,
    name: `Toolhead ${index}`,
    toolheadType: 'Physical',
    ...overrides,
  } as ToolheadDto;
}

function persistedGate(index: number, overrides: Partial<ToolheadDto> = {}): ToolheadDto {
  return toolhead(index, { toolheadType: 'MmuGate', name: `Gate ${index}`, ...overrides });
}

describe('resolveMaterialLoadout', () => {
  it('renders every slot the hardware reports, even when the config database has fewer', () => {
    // The Qidi Plus 4 defect: a four-slot QidiBox with only three gates persisted
    // used to render four swatches above and three assignment rows below.
    const loadout = resolveMaterialLoadout(
      mmu([gate(0), gate(1), gate(2), gate(3)], MmuProtocol.Qidibox),
      [toolhead(0), persistedGate(1), persistedGate(2), persistedGate(3)],
    );

    expect(loadout).not.toBeNull();
    expect(loadout!.slots).toHaveLength(4);
    expect(loadout!.slots.map((s) => s.label)).toEqual(['G1', 'G2', 'G3', 'G4']);
    expect(loadout!.unitLabel).toBe('QidiBox');
    expect(loadout!.kind).toBe('gate');
  });

  it('translates live gate indices to the 1-based indices the spool API persists', () => {
    // Live MMU status is 0-based; the backend stores gates at 1..N because the
    // shared hotend owns index 0. Getting this wrong wrote each assignment to the
    // neighbouring gate, which is what made the two panels disagree.
    const loadout = resolveMaterialLoadout(
      mmu([gate(0), gate(1), gate(2), gate(3)], MmuProtocol.Qidibox),
      [toolhead(0), persistedGate(1), persistedGate(2), persistedGate(3)],
    );

    expect(loadout!.slots.map((s) => s.gcodeIndex)).toEqual([0, 1, 2, 3]);
    expect(loadout!.slots.map((s) => s.apiIndex)).toEqual([1, 2, 3, 4]);
  });

  it('passes indices through unchanged when no gates are persisted yet', () => {
    const loadout = resolveMaterialLoadout(mmu([gate(0), gate(1)], MmuProtocol.Qidibox), undefined);

    expect(loadout!.slots.map((s) => s.apiIndex)).toEqual([0, 1]);
  });

  it('describes a Snapmaker U1 as toolheads rather than AMS gates', () => {
    // The U1 reports real toolheads over the MMU channel. Calling them "AMS 1"
    // and "Gate n" told the user something untrue about their machine.
    const loadout = resolveMaterialLoadout(
      mmu([gate(0), gate(1), gate(2), gate(3)], MmuProtocol.SnapmakerU1),
      [toolhead(0)],
    );

    expect(loadout!.kind).toBe('tool');
    expect(loadout!.unitLabel).toBe('Toolheads');
    expect(loadout!.slots.map((s) => s.label)).toEqual(['T0', 'T1', 'T2', 'T3']);
    expect(JSON.stringify(loadout)).not.toMatch(/AMS|Gate/);
  });

  it('does not shift indices on a toolchanger, whose toolheads are already 0-based', () => {
    const loadout = resolveMaterialLoadout(
      mmu([gate(0), gate(1)], MmuProtocol.SnapmakerU1),
      [toolhead(0), persistedGate(1)],
    );

    expect(loadout!.slots.map((s) => s.apiIndex)).toEqual([0, 1]);
    expect(loadout!.slots.map((s) => s.gcodeIndex)).toEqual([0, 1]);
  });

  it('sorts slots by index regardless of the order the device reports them', () => {
    const loadout = resolveMaterialLoadout(mmu([gate(2), gate(0), gate(1)]), undefined);

    expect(loadout!.slots.map((s) => s.gcodeIndex)).toEqual([0, 1, 2]);
    expect(loadout!.slots.map((s) => s.label)).toEqual(['G1', 'G2', 'G3']);
  });

  it('carries the loaded material and spool through to each slot', () => {
    const loadout = resolveMaterialLoadout(
      mmu([gate(0, { material: 'PETG', color: '#00ff00', spoolId: 42 }), gate(1)]),
      undefined,
    );

    expect(loadout!.slots[0]).toMatchObject({ material: 'PETG', color: '#00ff00', spoolId: 42 });
    expect(loadout!.slots[1].spoolId).toBeUndefined();
  });

  it('falls back to the persisted topology when the device reports no live gates', () => {
    const loadout = resolveMaterialLoadout(undefined, [
      toolhead(0, { name: 'Left', currentMaterial: 'PLA' }),
      toolhead(1, { name: 'Right', currentMaterial: 'PETG' }),
    ]);

    expect(loadout!.kind).toBe('tool');
    expect(loadout!.slots.map((s) => s.label)).toEqual(['T0', 'T1']);
    expect(loadout!.slots.map((s) => s.apiIndex)).toEqual([0, 1]);
  });

  it('maps persisted gates back to 0-based coverage indices', () => {
    const loadout = resolveMaterialLoadout(undefined, [
      toolhead(0),
      persistedGate(1, { currentMaterial: 'PLA' }),
      persistedGate(2),
    ]);

    expect(loadout!.kind).toBe('gate');
    expect(loadout!.slots.map((s) => s.apiIndex)).toEqual([1, 2]);
    expect(loadout!.slots.map((s) => s.gcodeIndex)).toEqual([0, 1]);
  });

  it('surfaces a directly fed hotend alongside the gates', () => {
    const loadout = resolveMaterialLoadout(undefined, [
      toolhead(0, { currentMaterial: 'ASA', currentSpoolId: 7 }),
      persistedGate(1),
    ]);

    const external = loadout!.slots.filter((s) => s.external);
    expect(external).toHaveLength(1);
    expect(external[0].material).toBe('ASA');
  });

  it('returns nothing for a printer with a single filament source', () => {
    expect(resolveMaterialLoadout(undefined, [toolhead(0)])).toBeNull();
    expect(resolveMaterialLoadout(undefined, undefined)).toBeNull();
    expect(resolveMaterialLoadout(mmu([]), undefined)).toBeNull();
  });

  it('names each vendor unit rather than defaulting everything to AMS', () => {
    const label = (mmuType?: string) =>
      resolveMaterialLoadout(mmu([gate(0)], mmuType), undefined)!.unitLabel;

    expect(label(MmuProtocol.Qidibox)).toBe('QidiBox');
    expect(label(MmuProtocol.Afc)).toBe('AFC');
    expect(label(MmuProtocol.HappyHare)).toBe('MMU');
    expect(label(undefined)).toBe('AMS');
  });
});

describe('isLightColor', () => {
  it('flags pale swatches that need a border to stay visible', () => {
    expect(isLightColor('#ffffff')).toBe(true);
    expect(isLightColor('#000000')).toBe(false);
    expect(isLightColor('#fff')).toBe(false);
  });
});
