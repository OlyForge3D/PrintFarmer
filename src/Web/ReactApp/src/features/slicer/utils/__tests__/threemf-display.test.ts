import { BufferGeometry, Float32BufferAttribute } from 'three';
import { describe, expect, it } from 'vitest';
import { cloneThreeMfMeshesDroppedToBed } from '@/features/slicer/utils/threemf-display';

function createTriangleGeometry(zOffset: number): BufferGeometry {
  const geometry = new BufferGeometry();
  geometry.setAttribute(
    'position',
    new Float32BufferAttribute([
      0, 0, zOffset,
      10, 0, zOffset,
      0, 5, zOffset + 2,
    ], 3),
  );
  geometry.computeBoundingBox();
  return geometry;
}

describe('cloneThreeMfMeshesDroppedToBed', () => {
  it('drops meshes with the same buildItemIndex as a unit (preserving relative Z)', () => {
    // Two meshes from the same build item (multi-material): Z ranges [12, 14] and [40, 42]
    // Within-group minZ = 12, shift by -12 → [0, 2] and [28, 30]
    const lower = createTriangleGeometry(12);
    const upper = createTriangleGeometry(40);
    const prepared = cloneThreeMfMeshesDroppedToBed([
      { geometry: lower, extruderIndex: 0, buildItemIndex: 0 },
      { geometry: upper, extruderIndex: 1, buildItemIndex: 0 },
    ]);

    expect(prepared.size.z).toBeCloseTo(30);
    expect(prepared.center.z).toBeCloseTo(15);

    const lowerResult = prepared.meshes[0];
    lowerResult.geometry.computeBoundingBox();
    expect(lowerResult.geometry.boundingBox?.min.z).toBeCloseTo(0);
    expect(lowerResult.geometry.boundingBox?.max.z).toBeCloseTo(2);

    const upperResult = prepared.meshes[1];
    upperResult.geometry.computeBoundingBox();
    expect(upperResult.geometry.boundingBox?.min.z).toBeCloseTo(28);
    expect(upperResult.geometry.boundingBox?.max.z).toBeCloseTo(30);

    // Originals are not mutated
    lower.computeBoundingBox();
    upper.computeBoundingBox();
    expect(lower.boundingBox?.min.z).toBeCloseTo(12);
    expect(upper.boundingBox?.min.z).toBeCloseTo(40);

    for (const mesh of prepared.meshes) {
      mesh.geometry.dispose();
    }
    lower.dispose();
    upper.dispose();
  });

  it('drops meshes with different buildItemIndex independently to bed', () => {
    // Two independent build items at different Z heights
    const meshA = createTriangleGeometry(12);
    const meshB = createTriangleGeometry(40);
    const prepared = cloneThreeMfMeshesDroppedToBed([
      { geometry: meshA, extruderIndex: 0, buildItemIndex: 0 },
      { geometry: meshB, extruderIndex: 1, buildItemIndex: 1 },
    ]);

    // Both meshes sit at Z=0 independently
    const resultA = prepared.meshes[0];
    resultA.geometry.computeBoundingBox();
    expect(resultA.geometry.boundingBox?.min.z).toBeCloseTo(0);
    expect(resultA.geometry.boundingBox?.max.z).toBeCloseTo(2);

    const resultB = prepared.meshes[1];
    resultB.geometry.computeBoundingBox();
    expect(resultB.geometry.boundingBox?.min.z).toBeCloseTo(0);
    expect(resultB.geometry.boundingBox?.max.z).toBeCloseTo(2);

    expect(prepared.size.z).toBeCloseTo(2);

    for (const mesh of prepared.meshes) {
      mesh.geometry.dispose();
    }
    meshA.dispose();
    meshB.dispose();
  });

  it('drops duplicate instances of same object independently (shared objectId, different buildItemIndex)', () => {
    // Two build items referencing the same source object but at different transforms
    // They share objectId but have different buildItemIndex — each drops independently
    const instance1 = createTriangleGeometry(5);
    const instance2 = createTriangleGeometry(20);
    const prepared = cloneThreeMfMeshesDroppedToBed([
      { geometry: instance1, extruderIndex: 0, buildItemIndex: 0 },
      { geometry: instance2, extruderIndex: 0, buildItemIndex: 1 },
    ]);

    const result1 = prepared.meshes[0];
    result1.geometry.computeBoundingBox();
    expect(result1.geometry.boundingBox?.min.z).toBeCloseTo(0);

    const result2 = prepared.meshes[1];
    result2.geometry.computeBoundingBox();
    expect(result2.geometry.boundingBox?.min.z).toBeCloseTo(0);

    for (const mesh of prepared.meshes) {
      mesh.geometry.dispose();
    }
    instance1.dispose();
    instance2.dispose();
  });

  it('falls back to global drop when no buildItemIndex is provided', () => {
    // Without buildItemIndex, all meshes go to a single group → global minZ drop
    const lower = createTriangleGeometry(12);
    const upper = createTriangleGeometry(40);
    const prepared = cloneThreeMfMeshesDroppedToBed([
      { geometry: lower, extruderIndex: 0 },
      { geometry: upper, extruderIndex: 1 },
    ]);

    expect(prepared.size.z).toBeCloseTo(30);

    const lowerResult = prepared.meshes[0];
    lowerResult.geometry.computeBoundingBox();
    expect(lowerResult.geometry.boundingBox?.min.z).toBeCloseTo(0);

    const upperResult = prepared.meshes[1];
    upperResult.geometry.computeBoundingBox();
    expect(upperResult.geometry.boundingBox?.min.z).toBeCloseTo(28);

    for (const mesh of prepared.meshes) {
      mesh.geometry.dispose();
    }
    lower.dispose();
    upper.dispose();
  });

  it('handles empty mesh array gracefully', () => {
    const prepared = cloneThreeMfMeshesDroppedToBed([]);
    expect(prepared.meshes).toHaveLength(0);
    expect(prepared.size.x).toBe(0);
    expect(prepared.size.y).toBe(0);
    expect(prepared.size.z).toBe(0);
  });
});
