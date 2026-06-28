import { describe, it, expect } from 'vitest';
import {
  isSliceableModel,
  modelTransformJson,
  buildSlicePayloadModels,
  diffProcessOverrides,
} from '../slicePayload';
import type { LoadedModel } from '@/features/slicer/components/viewer/SlicerBedVisualization';

function model(overrides: Partial<LoadedModel> & { id: string }): LoadedModel {
  return {
    url: `https://cdn.example.com/${overrides.id}.stl`,
    fileName: `${overrides.id}.stl`,
    fileType: 'stl',
    position: [0, 0, 0],
    rotation: [0, 0, 0],
    scale: [1, 1, 1],
    ...overrides,
  };
}

describe('slicePayload', () => {
  describe('isSliceableModel', () => {
    it('treats server-hosted URLs as sliceable', () => {
      expect(isSliceableModel(model({ id: 'a' }))).toBe(true);
    });

    it('rejects blob URLs and empty URLs', () => {
      expect(isSliceableModel(model({ id: 'b', url: 'blob:abc123' }))).toBe(false);
      expect(isSliceableModel(model({ id: 'c', url: '' }))).toBe(false);
    });
  });

  describe('modelTransformJson', () => {
    it('serializes rotation, scale and position', () => {
      const json = modelTransformJson(model({ id: 'a', position: [1, 2, 3], rotation: [0.1, 0, 0], scale: [2, 2, 2] }));
      expect(JSON.parse(json)).toEqual({
        rotation: [0.1, 0, 0],
        scale: [2, 2, 2],
        position: [1, 2, 3],
      });
    });
  });

  describe('buildSlicePayloadModels', () => {
    it('reports an empty active plate as not sliceable', () => {
      const result = buildSlicePayloadModels([]);
      expect(result.primary).toBeNull();
      expect(result.sliceableCount).toBe(0);
      expect(result.modelFileUrls).toBeUndefined();
    });

    it('single-model path: primary is the first sliceable model, no multi arrays', () => {
      const result = buildSlicePayloadModels([model({ id: 'only' })]);
      expect(result.primary?.id).toBe('only');
      expect(result.sliceableCount).toBe(1);
      expect(result.modelFileUrls).toBeUndefined();
      expect(result.modelFileTransforms).toBeUndefined();
    });

    it('multi path: only active-plate sliceable URLs/transforms are included', () => {
      const result = buildSlicePayloadModels([
        model({ id: 'a' }),
        model({ id: 'b', url: 'blob:pending' }), // failed upload — filtered out
        model({ id: 'c' }),
      ]);
      expect(result.sliceableCount).toBe(2);
      expect(result.modelFileUrls).toEqual([
        'https://cdn.example.com/a.stl',
        'https://cdn.example.com/c.stl',
      ]);
      expect(result.modelFileTransforms).toHaveLength(2);
      expect(result.primary?.id).toBe('a');
    });
  });

  describe('diffProcessOverrides', () => {
    it('returns only the keys whose value changed from the baseline', () => {
      const original = { wall_loops: 2, layer_height: 0.2, sparse_infill_density: 15 };
      const current = { wall_loops: 4, layer_height: 0.2, sparse_infill_density: 15 };

      expect(diffProcessOverrides(current, original)).toEqual({ wall_loops: 4 });
    });

    it('returns an empty object when nothing was modified (no default leakage)', () => {
      const baseline = { wall_loops: 2, layer_height: 0.2, top_shell_layers: 4, enable_support: false };

      // Unmodified profile → no overrides, so the worker keeps inherited values.
      expect(diffProcessOverrides({ ...baseline }, baseline)).toEqual({});
    });

    it('treats equal-but-different-typed values as unchanged via JSON comparison', () => {
      // Both sides are seeded from the same coerced object, so matching native
      // values never produce a spurious override.
      const original = { layer_height: 0.2, enable_support: false };
      const current = { layer_height: 0.2, enable_support: false };

      expect(diffProcessOverrides(current, original)).toEqual({});
    });

    it('includes a key whose baseline was undefined once the user sets it', () => {
      expect(diffProcessOverrides({ brim_width: 5 }, {})).toEqual({ brim_width: 5 });
    });

    it('includes boolean and string edits, not just numbers', () => {
      const original = { enable_support: false, support_type: 'normal(auto)' };
      const current = { enable_support: true, support_type: 'tree(auto)' };

      expect(diffProcessOverrides(current, original)).toEqual({
        enable_support: true,
        support_type: 'tree(auto)',
      });
    });
  });
});
