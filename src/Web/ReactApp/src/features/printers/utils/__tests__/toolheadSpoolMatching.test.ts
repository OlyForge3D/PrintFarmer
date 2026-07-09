import { describe, expect, it } from 'vitest';
import {
  assignSpoolsToToolheads,
  buildFilamentMatchTargets,
  getSpoolMatchConfidence,
  hasMaterialMismatch,
} from '../toolheadSpoolMatching';

describe('toolheadSpoolMatching', () => {
  it('matches exact color and material without a mismatch', () => {
    const [match] = assignSpoolsToToolheads(
      buildFilamentMatchTargets(['#FF0000'], ['PLA']),
      [{ spoolId: 10, colorHex: '#FF0000', material: 'PLA' }],
    );

    expect(match.spoolId).toBe(10);
    expect(match.confidence).toBe('exact');
    expect(match.materialMismatch).toBe(false);
    expect(match.deltaE).toBeCloseTo(0, 6);
  });

  it('chooses deterministic lower spool IDs for equal-distance ties', () => {
    const [match] = assignSpoolsToToolheads(
      buildFilamentMatchTargets(['#00FF00'], ['PLA']),
      [
        { spoolId: 42, colorHex: '#00FF00', material: 'PLA' },
        { spoolId: 7, colorHex: '#00FF00', material: 'PLA' },
      ],
    );

    expect(match.spoolId).toBe(7);
  });

  it('preserves empty-string positional slots without shifting later extruders', () => {
    const matches = assignSpoolsToToolheads(
      buildFilamentMatchTargets(['#FF0000', '', '#0000FF'], ['PLA', 'PLA', 'PETG']),
      [
        { spoolId: 1, colorHex: '#FF0000', material: 'PLA' },
        { spoolId: 2, colorHex: '#0000FF', material: 'PETG' },
      ],
    );

    expect(matches).toHaveLength(3);
    expect(matches[0].spoolId).toBe(1);
    expect(matches[1].toolheadIndex).toBe(1);
    expect(matches[1].spoolId).toBeNull();
    expect(matches[2].toolheadIndex).toBe(2);
    expect(matches[2].spoolId).toBe(2);
  });

  it('handles missing per-extruder color fields without suggestions', () => {
    const matches = assignSpoolsToToolheads(
      buildFilamentMatchTargets(undefined, ['PLA']),
      [{ spoolId: 1, colorHex: '#FF0000', material: 'PLA' }],
    );

    expect(matches).toEqual([]);
  });

  it('uses one-to-one exclusion when two file tools prefer the same spool', () => {
    const matches = assignSpoolsToToolheads(
      buildFilamentMatchTargets(['#FF0000', '#FF0101'], ['PLA', 'PLA']),
      [
        { spoolId: 1, colorHex: '#FF0000', material: 'PLA' },
        { spoolId: 2, colorHex: '#00FF00', material: 'PLA' },
      ],
    );

    expect(matches[0].spoolId).not.toBeNull();
    expect(matches[1].spoolId).not.toBeNull();
    expect(matches[0].spoolId).not.toBe(matches[1].spoolId);
  });

  it('flags material mismatch only when both materials are known and different', () => {
    const [match] = assignSpoolsToToolheads(
      buildFilamentMatchTargets(['#FF0000'], ['PETG']),
      [{ spoolId: 1, colorHex: '#FF0000', material: 'PLA' }],
    );

    expect(match.materialMismatch).toBe(true);
    expect(hasMaterialMismatch('PLA', ' pla ')).toBe(false);
    expect(hasMaterialMismatch('PLA', undefined)).toBe(false);
  });

  it('maps color distances to confidence tiers', () => {
    expect(getSpoolMatchConfidence(0)).toBe('exact');
    expect(getSpoolMatchConfidence(6)).toBe('close');
    expect(getSpoolMatchConfidence(20)).toBe('poor');
    expect(getSpoolMatchConfidence(null)).toBe('unknown');
  });
});
