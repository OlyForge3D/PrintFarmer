import { describe, it, expect } from 'vitest';
import * as THREE from 'three';
import {
  splitGeometryAtPlane,
  earClipTriangulate,
  orderCapEdges,
  filterDegenerateTriangles,
} from '../cutGeometry';

/** Compute signed triangle area in the plane normal to `axis`. */
function triArea(
  a: THREE.Vector3,
  b: THREE.Vector3,
  c: THREE.Vector3,
  axis: 'x' | 'y' | 'z',
): number {
  const proj = (v: THREE.Vector3): [number, number] =>
    axis === 'x' ? [v.y, v.z] : axis === 'y' ? [v.z, v.x] : [v.x, v.y];
  const [ax, ay] = proj(a);
  const [bx, by] = proj(b);
  const [cx, cy] = proj(c);
  return 0.5 * Math.abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay));
}

describe('splitGeometryAtPlane', () => {
  it('splits a unit cube at z=0 into two non-empty roughly equal halves', () => {
    const geo = new THREE.BoxGeometry(1, 1, 1);
    const { above, below } = splitGeometryAtPlane(geo, 'z', 0);
    const aboveCount = above.getAttribute('position').count;
    const belowCount = below.getAttribute('position').count;
    expect(aboveCount).toBeGreaterThan(0);
    expect(belowCount).toBeGreaterThan(0);
    // Roughly equal (within 50%)
    const ratio = Math.min(aboveCount, belowCount) / Math.max(aboveCount, belowCount);
    expect(ratio).toBeGreaterThanOrEqual(0.5);
  });

  it('cut entirely above the geometry leaves above empty and below populated', () => {
    const geo = new THREE.BoxGeometry(1, 1, 1);
    const { above, below } = splitGeometryAtPlane(geo, 'z', 10);
    expect(above.getAttribute('position').count).toBe(0);
    // Cube has 12 triangles → 36 vertices in below.
    expect(below.getAttribute('position').count).toBe(36);
  });

  it('cut entirely below the geometry leaves below empty and above populated', () => {
    const geo = new THREE.BoxGeometry(1, 1, 1);
    const { above, below } = splitGeometryAtPlane(geo, 'z', -10);
    expect(below.getAttribute('position').count).toBe(0);
    expect(above.getAttribute('position').count).toBe(36);
  });

  it('respects identity matrixWorld of a default Mesh wrapping the geometry', () => {
    const geo = new THREE.BoxGeometry(1, 1, 1);
    const mesh = new THREE.Mesh(geo);
    mesh.updateMatrixWorld(true);
    const { above, below } = splitGeometryAtPlane(geo, 'z', 0, mesh.matrixWorld);
    expect(above.getAttribute('position').count).toBeGreaterThan(0);
    expect(below.getAttribute('position').count).toBeGreaterThan(0);
  });
});

describe('earClipTriangulate', () => {
  it('triangulates a CCW square (z plane) into exactly 2 triangles', () => {
    const square = [
      new THREE.Vector3(0, 0, 0),
      new THREE.Vector3(1, 0, 0),
      new THREE.Vector3(1, 1, 0),
      new THREE.Vector3(0, 1, 0),
    ];
    const tris = earClipTriangulate(square, 'z');
    expect(tris).toHaveLength(2);
  });

  it('triangulates a concave L-shape (6 verts, CCW, z plane) into 4 non-degenerate triangles', () => {
    // L-shape, CCW:
    // (0,0) → (2,0) → (2,1) → (1,1) → (1,2) → (0,2) → (0,0)
    const lshape = [
      new THREE.Vector3(0, 0, 0),
      new THREE.Vector3(2, 0, 0),
      new THREE.Vector3(2, 1, 0),
      new THREE.Vector3(1, 1, 0),
      new THREE.Vector3(1, 2, 0),
      new THREE.Vector3(0, 2, 0),
    ];
    const tris = earClipTriangulate(lshape, 'z');
    expect(tris).toHaveLength(4);
    for (const [a, b, c] of tris) {
      expect(triArea(a, b, c, 'z')).toBeGreaterThan(1e-9);
    }
  });

  it('output triangles only reference vertices from the input polygon', () => {
    const square = [
      new THREE.Vector3(0, 0, 0),
      new THREE.Vector3(1, 0, 0),
      new THREE.Vector3(1, 1, 0),
      new THREE.Vector3(0, 1, 0),
    ];
    const tris = earClipTriangulate(square, 'z');
    const inputSet = new Set(square);
    for (const tri of tris) {
      for (const v of tri) {
        expect(inputSet.has(v)).toBe(true);
      }
    }
  });

  it('returns empty for fewer than 3 vertices', () => {
    expect(earClipTriangulate([], 'z')).toEqual([]);
    expect(earClipTriangulate([new THREE.Vector3(0, 0, 0)], 'z')).toEqual([]);
    expect(earClipTriangulate(
      [new THREE.Vector3(0, 0, 0), new THREE.Vector3(1, 0, 0)],
      'z',
    )).toEqual([]);
  });
});

describe('orderCapEdges', () => {
  it('returns a single 4-vertex polygon for a closed square loop', () => {
    const v0 = new THREE.Vector3(0, 0, 0);
    const v1 = new THREE.Vector3(1, 0, 0);
    const v2 = new THREE.Vector3(1, 1, 0);
    const v3 = new THREE.Vector3(0, 1, 0);
    const edges: Array<[THREE.Vector3, THREE.Vector3]> = [
      [v0, v1],
      [v1, v2],
      [v2, v3],
      [v3, v0],
    ];
    const loops = orderCapEdges(edges);
    expect(loops).toHaveLength(1);
    expect(loops[0]).toHaveLength(4);
  });

  it('returns two 4-vertex polygons for two disjoint squares', () => {
    // First square at origin
    const a0 = new THREE.Vector3(0, 0, 0);
    const a1 = new THREE.Vector3(1, 0, 0);
    const a2 = new THREE.Vector3(1, 1, 0);
    const a3 = new THREE.Vector3(0, 1, 0);
    // Second square far away
    const b0 = new THREE.Vector3(10, 10, 0);
    const b1 = new THREE.Vector3(11, 10, 0);
    const b2 = new THREE.Vector3(11, 11, 0);
    const b3 = new THREE.Vector3(10, 11, 0);
    const edges: Array<[THREE.Vector3, THREE.Vector3]> = [
      [a0, a1], [a1, a2], [a2, a3], [a3, a0],
      [b0, b1], [b1, b2], [b2, b3], [b3, b0],
    ];
    const loops = orderCapEdges(edges);
    expect(loops).toHaveLength(2);
    expect(loops[0]).toHaveLength(4);
    expect(loops[1]).toHaveLength(4);
  });

  it('returns empty for empty input', () => {
    expect(orderCapEdges([])).toEqual([]);
  });
});

describe('filterDegenerateTriangles', () => {
  it('removes a triangle whose three vertices are identical', () => {
    const verts = [
      // Valid triangle
      0, 0, 0, 1, 0, 0, 0, 1, 0,
      // Degenerate triangle (all three vertices identical)
      5, 5, 5, 5, 5, 5, 5, 5, 5,
    ];
    const result = filterDegenerateTriangles(verts);
    expect(result).toHaveLength(9);
    expect(result).toEqual([0, 0, 0, 1, 0, 0, 0, 1, 0]);
  });

  it('keeps all triangles when none are degenerate', () => {
    const verts = [
      0, 0, 0, 1, 0, 0, 0, 1, 0,
      0, 0, 1, 1, 0, 1, 0, 1, 1,
    ];
    const result = filterDegenerateTriangles(verts);
    expect(result).toHaveLength(verts.length);
    expect(result).toEqual(verts);
  });

  it('returns empty for empty input', () => {
    expect(filterDegenerateTriangles([])).toEqual([]);
  });
});
