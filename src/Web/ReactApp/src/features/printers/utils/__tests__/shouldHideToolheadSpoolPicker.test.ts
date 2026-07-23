import { describe, it, expect } from 'vitest';
import { shouldHideToolheadSpoolPicker } from '../shouldHideToolheadSpoolPicker';
import { MmuGateStatus, type MmuGate, type ToolheadDto } from '@/types/api';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';

function makeGate(index: number): MmuGate {
  return { index, status: MmuGateStatus.Empty, spoolId: 0 };
}

function makeToolhead(index: number, toolheadType: 'Physical' | 'MmuGate'): ToolheadDto {
  return { id: `th-${index}`, index, isPrimary: index === 0, toolheadType };
}

describe('shouldHideToolheadSpoolPicker', () => {
  it('returns false when nothing is provided (single-spool printer)', () => {
    expect(shouldHideToolheadSpoolPicker(undefined, undefined)).toBe(false);
  });

  it('returns true when live MMU gates are present (Klipper Happy-Hare path)', () => {
    expect(shouldHideToolheadSpoolPicker([makeGate(0), makeGate(1)], undefined)).toBe(true);
  });

  it('returns false for live Snapmaker U1 lanes because they are physical toolheads', () => {
    const toolheads = [makeToolhead(0, 'Physical'), makeToolhead(1, 'Physical')];

    expect(shouldHideToolheadSpoolPicker([makeGate(0), makeGate(1)], toolheads, MmuProtocol.SnapmakerU1)).toBe(false);
  });

  it('returns false when live MMU gates array is empty', () => {
    expect(shouldHideToolheadSpoolPicker([], undefined)).toBe(false);
  });

  it('returns true when persisted toolheads include MmuGate entries (Bambu AMS path)', () => {
    const toolheads = [
      makeToolhead(0, 'MmuGate'),
      makeToolhead(1, 'MmuGate'),
      makeToolhead(2, 'MmuGate'),
      makeToolhead(3, 'MmuGate'),
    ];
    expect(shouldHideToolheadSpoolPicker(undefined, toolheads)).toBe(true);
  });

  it('returns false for physical-only multi-toolhead printers (e.g., Snapmaker U1)', () => {
    const toolheads = [makeToolhead(0, 'Physical'), makeToolhead(1, 'Physical')];
    expect(shouldHideToolheadSpoolPicker(undefined, toolheads)).toBe(false);
  });

  it('returns true when toolheads mix Physical and MmuGate (any MmuGate triggers hide)', () => {
    const toolheads = [makeToolhead(0, 'Physical'), makeToolhead(1, 'MmuGate')];
    expect(shouldHideToolheadSpoolPicker(undefined, toolheads)).toBe(true);
  });

  it('returns false for a single physical toolhead (single-spool default)', () => {
    expect(shouldHideToolheadSpoolPicker(undefined, [makeToolhead(0, 'Physical')])).toBe(false);
  });
});
