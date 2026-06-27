import { describe, it, expect } from 'vitest';
import {
  createInitialPlateState,
  addPlate,
  removePlate,
  setActivePlate,
  addModelToActivePlate,
  getModelsForPlate,
  getPlateForModel,
  duplicatePlate,
  replaceModelOnSamePlate,
  computePlateLayout,
  type PlateManagerState,
} from '../plateManager';

/** Build a state with `count` plates (1..count), making the last one active. */
function makePlates(count: number): PlateManagerState {
  let state = createInitialPlateState();
  for (let i = 1; i < count; i++) {
    state = addPlate(state);
  }
  return state;
}

describe('plateManager', () => {
  describe('addPlate', () => {
    it('adds a plate and auto-activates it', () => {
      const s0 = createInitialPlateState();
      const s1 = addPlate(s0);
      expect(s1.plates).toHaveLength(2);
      expect(s1.activePlateId).toBe(s1.plates[1].id);
      expect(s1.plates[1].name).toBe('Plate 2');
      expect(s1.plates[1].modelIds).toEqual([]);
    });

    it('caps at MAX 10 plates (no-op at the cap)', () => {
      const s = makePlates(10);
      expect(s.plates).toHaveLength(10);
      const capped = addPlate(s);
      expect(capped).toBe(s); // returns same reference (no change)
      expect(capped.plates).toHaveLength(10);
    });
  });

  describe('removePlate', () => {
    it('refuses to remove the last remaining plate (min 1)', () => {
      const s = createInitialPlateState();
      const result = removePlate(s, s.plates[0].id);
      expect(result).toBe(s);
      expect(result.plates).toHaveLength(1);
    });

    it('migrates orphaned model ids to the new active plate', () => {
      let s = createInitialPlateState();
      const firstId = s.plates[0].id;
      s = addModelToActivePlate(s, 'm-a'); // m-a on plate 1
      s = addPlate(s); // plate 2 active
      s = addModelToActivePlate(s, 'm-b'); // m-b on plate 2
      // Remove plate 1 (non-active) — its m-a should move to active plate 2.
      const result = removePlate(s, firstId);
      expect(result.plates).toHaveLength(1);
      expect(result.plates[0].modelIds).toEqual(expect.arrayContaining(['m-a', 'm-b']));
    });

    it('moves active to a remaining plate when the active plate is removed', () => {
      const s = makePlates(3); // plate 3 active
      const activeId = s.activePlateId;
      const result = removePlate(s, activeId);
      expect(result.plates).toHaveLength(2);
      expect(result.activePlateId).toBe(result.plates[0].id);
    });
  });

  describe('getModelsForPlate', () => {
    it('returns the plate model ids, and [] for an unknown plate', () => {
      let s = createInitialPlateState();
      const id = s.plates[0].id;
      s = addModelToActivePlate(s, 'x');
      s = addModelToActivePlate(s, 'y');
      expect(getModelsForPlate(s, id)).toEqual(['x', 'y']);
      expect(getModelsForPlate(s, 'nonexistent')).toEqual([]);
    });
  });

  describe('duplicatePlate', () => {
    it('creates an empty copy (no shared model refs) and activates it', () => {
      let s = createInitialPlateState();
      s = addModelToActivePlate(s, 'm1');
      const sourceId = s.plates[0].id;
      const result = duplicatePlate(s, sourceId);
      expect(result.plates).toHaveLength(2);
      const copy = result.plates[1];
      expect(copy.modelIds).toEqual([]);
      expect(copy.name).toContain('empty copy');
      expect(result.activePlateId).toBe(copy.id);
    });
  });

  describe('replaceModelOnSamePlate', () => {
    it('keeps the new pieces on the SOURCE plate, not the active plate', () => {
      let s = createInitialPlateState();
      const plate1 = s.plates[0].id;
      s = addModelToActivePlate(s, 'orig'); // orig on plate 1
      s = addPlate(s); // plate 2 now active
      // Cut completes while plate 2 is active; pieces must stay on plate 1.
      const result = replaceModelOnSamePlate(s, 'orig', ['piece-a', 'piece-b']);
      expect(getModelsForPlate(result, plate1)).toEqual(['piece-a', 'piece-b']);
      expect(getPlateForModel(result, 'piece-a')).toBe(plate1);
      // orig is gone everywhere
      expect(getPlateForModel(result, 'orig')).toBeNull();
    });

    it('falls back to the active plate when the removed id is not found', () => {
      let s = createInitialPlateState();
      s = addPlate(s); // plate 2 active, empty
      const activeId = s.activePlateId;
      const result = replaceModelOnSamePlate(s, 'ghost', ['new1']);
      expect(getModelsForPlate(result, activeId)).toContain('new1');
    });

    it('does not duplicate ids already present on the target plate', () => {
      let s = createInitialPlateState();
      const plate1 = s.plates[0].id;
      s = addModelToActivePlate(s, 'orig');
      s = addModelToActivePlate(s, 'keep');
      const result = replaceModelOnSamePlate(s, 'orig', ['keep', 'new']);
      const ids = getModelsForPlate(result, plate1);
      expect(ids.filter(id => id === 'keep')).toHaveLength(1);
      expect(ids).toContain('new');
    });
  });

  describe('setActivePlate', () => {
    it('ignores unknown plate ids', () => {
      const s = makePlates(2);
      expect(setActivePlate(s, 'nope')).toBe(s);
    });
  });
});

describe('computePlateLayout', () => {
  const W = 256;
  const D = 256;
  const EPS = 1e-9;

  const centroid = (offs: Array<[number, number, number]>) => {
    const sx = offs.reduce((s, o) => s + o[0], 0) / offs.length;
    const sy = offs.reduce((s, o) => s + o[1], 0) / offs.length;
    return [sx, sy];
  };

  it('n=1 → single zero offset (legacy single-plate parity)', () => {
    expect(computePlateLayout(1, W, D)).toEqual([[0, 0, 0]]);
  });

  it('n<=0 is clamped to a single zero offset', () => {
    expect(computePlateLayout(0, W, D)).toEqual([[0, 0, 0]]);
    expect(computePlateLayout(-3, W, D)).toEqual([[0, 0, 0]]);
  });

  it('always returns translation-only offsets (z === 0)', () => {
    for (const n of [1, 2, 4, 5, 9, 10]) {
      for (const o of computePlateLayout(n, W, D)) {
        expect(o[2]).toBe(0);
      }
    }
  });

  it('returns max(n,1) offsets', () => {
    expect(computePlateLayout(4, W, D)).toHaveLength(4);
    expect(computePlateLayout(10, W, D)).toHaveLength(10);
  });

  it('centroid is exactly (0,0) for full and partial grids', () => {
    for (const n of [2, 3, 4, 5, 6, 7, 8, 9, 10]) {
      const [cx, cy] = centroid(computePlateLayout(n, W, D));
      expect(Math.abs(cx)).toBeLessThan(EPS);
      expect(Math.abs(cy)).toBeLessThan(EPS);
    }
  });

  it('n=4 is a symmetric 2x2 grid with the expected spacing', () => {
    const spacing = Math.max(W, D) * 1.12;
    const offs = computePlateLayout(4, W, D);
    const xs = offs.map(o => o[0]).sort((a, b) => a - b);
    const ys = offs.map(o => o[1]).sort((a, b) => a - b);
    expect(xs).toEqual([-spacing / 2, -spacing / 2, spacing / 2, spacing / 2]);
    expect(ys).toEqual([-spacing / 2, -spacing / 2, spacing / 2, spacing / 2]);
  });

  it('uses ceil(sqrt(n)) columns', () => {
    // n=5 → cols=3: first row spans 3 distinct x values.
    const offs = computePlateLayout(5, W, D);
    const firstRowX = new Set(offs.slice(0, 3).map(o => o[0]));
    expect(firstRowX.size).toBe(3);
  });

  it('spacing scales with the larger bed dimension', () => {
    const offs = computePlateLayout(2, 100, 300);
    const spacing = 300 * 1.12;
    const xs = offs.map(o => o[0]).sort((a, b) => a - b);
    expect(xs).toEqual([-spacing / 2, spacing / 2]);
  });
});
