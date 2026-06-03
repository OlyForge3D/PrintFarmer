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
  it('drops the assembly to the bed while preserving relative Z-positioning', () => {
    // lower mesh: Z range [12, 14], upper mesh: Z range [40, 42]
    // Global minZ = 12, so entire assembly shifts down by 12
    // Result: lower → [0, 2], upper → [28, 30]
    const lower = createTriangleGeometry(12);
    const upper = createTriangleGeometry(40);
    const prepared = cloneThreeMfMeshesDroppedToBed([
      { geometry: lower, extruderIndex: 0 },
      { geometry: upper, extruderIndex: 1 },
    ]);

    // Combined bounds: Z range [0, 30], size.z = 30, center.z = 15
    expect(prepared.size.z).toBeCloseTo(30);
    expect(prepared.center.z).toBeCloseTo(15);

    // Lower mesh sits on the bed
    const lowerResult = prepared.meshes[0];
    lowerResult.geometry.computeBoundingBox();
    expect(lowerResult.geometry.boundingBox?.min.z).toBeCloseTo(0);
    expect(lowerResult.geometry.boundingBox?.max.z).toBeCloseTo(2);

    // Upper mesh preserves 28mm gap above lower mesh
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
  });

  it('handles empty mesh array gracefully', () => {
    const prepared = cloneThreeMfMeshesDroppedToBed([]);
    expect(prepared.meshes).toHaveLength(0);
    expect(prepared.size.x).toBe(0);
    expect(prepared.size.y).toBe(0);
    expect(prepared.size.z).toBe(0);
  });
});
