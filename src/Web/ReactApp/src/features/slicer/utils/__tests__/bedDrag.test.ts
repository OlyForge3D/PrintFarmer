import { describe, it, expect } from 'vitest';
import { localizeDragTarget } from '../bedDrag';

const BED = { width: 256, depth: 256 };

describe('localizeDragTarget', () => {
  it('adds the grab offset to the world hit (offset cancels the plate translation)', () => {
    // grabOffset = model.position(local) - hit(world). For a model on a plate
    // translated by +100 in X, a local position of 10 means the world grab hit
    // was at 110, so offset = 10 - 110 = -100. Re-applying to a fresh world hit
    // of 130 must yield the bed-local 30 — the plate offset never appears.
    const [nx, ny] = localizeDragTarget({ x: 130, y: 0 }, { x: -100, y: 0 }, BED);
    expect(nx).toBe(30);
    expect(ny).toBe(0);
  });

  it('is identical regardless of which plate the model sits on', () => {
    // Same pointer drag, two plates offset differently. The world hit differs by
    // the plate offset and the captured grab offset differs by the same amount,
    // so the resolved bed-local position is identical.
    const plateA = localizeDragTarget({ x: 50, y: 20 }, { x: -10, y: -5 }, BED);
    // Plate B sits +200 further in world X; both hit and offset shift by +200.
    const plateB = localizeDragTarget({ x: 250, y: 20 }, { x: -210, y: -5 }, BED);
    expect(plateB).toEqual(plateA);
  });

  it('clamps to the bed half-extents', () => {
    expect(localizeDragTarget({ x: 10000, y: 10000 }, { x: 0, y: 0 }, BED))
      .toEqual([128, 128]);
    expect(localizeDragTarget({ x: -10000, y: -10000 }, { x: 0, y: 0 }, BED))
      .toEqual([-128, -128]);
  });

  it('leaves in-bounds positions unclamped', () => {
    expect(localizeDragTarget({ x: 40, y: -60 }, { x: 0, y: 0 }, BED))
      .toEqual([40, -60]);
  });

  it('respects non-square beds per axis', () => {
    const bed = { width: 100, depth: 300 };
    expect(localizeDragTarget({ x: 999, y: 999 }, { x: 0, y: 0 }, bed))
      .toEqual([50, 150]);
  });
});
