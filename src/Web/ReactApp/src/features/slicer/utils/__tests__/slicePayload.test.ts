import { describe, it, expect } from 'vitest';
import {
  isSliceableModel,
  modelTransformJson,
  buildSlicePayloadModels,
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
});
