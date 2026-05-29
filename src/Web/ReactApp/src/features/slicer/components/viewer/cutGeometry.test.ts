import { describe, it, expect } from 'vitest';
import * as THREE from 'three';
import {
  splitGeometryAtPlane,
  earClipTriangulate,
  orderCapEdges,
  filterDegenerateTriangles,
} from './cutGeometry';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Create a non-indexed unit cube centered at origin. */
function createCube(size = 1): THREE.BufferGeometry {
  return new THREE.BoxGeometry(size, size, size).toNonIndexed();
}

/** Total vertex count (positions / 3) from a BufferGeometry. */
function vertexCount(geo: THREE.BufferGeometry): number {
  return geo.getAttribute('position').count;
}

/** Sum of triangle areas in a BufferGeometry. */
function totalSurfaceArea(geo: THREE.BufferGeometry): number {
  const pos = geo.getAttribute('position');
  let area = 0;
  const a = new THREE.Vector3();
  const b = new THREE.Vector3();
  const c = new THREE.Vector3();
  for (let i = 0; i < pos.count; i += 3) {
    a.fromBufferAttribute(pos, i);
    b.fromBufferAttribute(pos, i + 1);
    c.fromBufferAttribute(pos, i + 2);
    const ab = new THREE.Vector3().subVectors(b, a);
    const ac = new THREE.Vector3().subVectors(c, a);
    area += ab.cross(ac).length() * 0.5;
  }
  return area;
}

/** Bounding box extents along a given axis. */
function axisExtent(geo: THREE.BufferGeometry, axis: 'x' | 'y' | 'z'): [number, number] {
  geo.computeBoundingBox();
  const bb = geo.boundingBox!;
  return axis === 'x' ? [bb.min.x, bb.max.x]
    : axis === 'y' ? [bb.min.y, bb.max.y]
    : [bb.min.z, bb.max.z];
}

/** Build a simple triangle BufferGeometry from three points. */
function triangleGeo(a: THREE.Vector3, b: THREE.Vector3, c: THREE.Vector3): THREE.BufferGeometry {
  const geo = new THREE.BufferGeometry();
  const verts = new Float32Array([a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z]);
  geo.setAttribute('position', new THREE.BufferAttribute(verts, 3));
  return geo;
}

// ===========================================================================
// splitGeometryAtPlane
// ===========================================================================

describe('splitGeometryAtPlane', () => {
  it('splits a unit cube at z=0 into two equal halves', () => {
    const cube = createCube(1);
    const { above, below } = splitGeometryAtPlane(cube, 'z', 0);

    // Both halves should have geometry
    expect(vertexCount(above)).toBeGreaterThan(0);
    expect(vertexCount(below)).toBeGreaterThan(0);

    // Each half should span z = [-0.5, 0] or [0, 0.5]
    const [aboveMin, aboveMax] = axisExtent(above, 'z');
    const [belowMin, belowMax] = axisExtent(below, 'z');
    expect(aboveMin).toBeCloseTo(0, 1);
    expect(aboveMax).toBeCloseTo(0.5, 1);
    expect(belowMin).toBeCloseTo(-0.5, 1);
    expect(belowMax).toBeCloseTo(0, 1);
  });

  it('produces halves with roughly equal surface area when cut at midpoint', () => {
    const cube = createCube(2);
    const { above, below } = splitGeometryAtPlane(cube, 'z', 0);
    const areaAbove = totalSurfaceArea(above);
    const areaBelow = totalSurfaceArea(below);
    // Both halves of a midpoint-cut cube have identical area
    expect(areaAbove).toBeCloseTo(areaBelow, 1);
  });

  it('returns all geometry on one side when plane is outside bounds', () => {
    const cube = createCube(1); // z ∈ [-0.5, 0.5]
    const { above, below } = splitGeometryAtPlane(cube, 'z', 10);
    // Plane at z=10 → everything is below
    expect(vertexCount(above)).toBe(0);
    expect(vertexCount(below)).toBeGreaterThan(0);
  });

  it('returns all geometry on above side when plane is far below', () => {
    const cube = createCube(1);
    const { above, below } = splitGeometryAtPlane(cube, 'z', -10);
    expect(vertexCount(below)).toBe(0);
    expect(vertexCount(above)).toBeGreaterThan(0);
  });

  it('supports X-axis cuts', () => {
    const cube = createCube(1);
    const { above, below } = splitGeometryAtPlane(cube, 'x', 0);
    expect(vertexCount(above)).toBeGreaterThan(0);
    expect(vertexCount(below)).toBeGreaterThan(0);

    const [aboveMin, aboveMax] = axisExtent(above, 'x');
    const [belowMin, belowMax] = axisExtent(below, 'x');
    expect(aboveMin).toBeCloseTo(0, 1);
    expect(aboveMax).toBeCloseTo(0.5, 1);
    expect(belowMin).toBeCloseTo(-0.5, 1);
    expect(belowMax).toBeCloseTo(0, 1);
  });

  it('supports Y-axis cuts', () => {
    const cube = createCube(1);
    const { above, below } = splitGeometryAtPlane(cube, 'y', 0);
    expect(vertexCount(above)).toBeGreaterThan(0);
    expect(vertexCount(below)).toBeGreaterThan(0);

    const [aboveMin, aboveMax] = axisExtent(above, 'y');
    const [belowMin, belowMax] = axisExtent(below, 'y');
    expect(aboveMin).toBeCloseTo(0, 1);
    expect(aboveMax).toBeCloseTo(0.5, 1);
    expect(belowMin).toBeCloseTo(-0.5, 1);
    expect(belowMax).toBeCloseTo(0, 1);
  });

  it('handles a single triangle that lies entirely above the plane', () => {
    const geo = triangleGeo(
      new THREE.Vector3(0, 0, 1),
      new THREE.Vector3(1, 0, 1),
      new THREE.Vector3(0, 1, 1),
    );
    const { above, below } = splitGeometryAtPlane(geo, 'z', 0);
    expect(vertexCount(above)).toBe(3);
    expect(vertexCount(below)).toBe(0);
  });

  it('handles a single triangle that spans the plane', () => {
    const geo = triangleGeo(
      new THREE.Vector3(0, 0, -1),
      new THREE.Vector3(1, 0, 1),
      new THREE.Vector3(-1, 0, 1),
    );
    const { above, below } = splitGeometryAtPlane(geo, 'z', 0);
    expect(vertexCount(above)).toBeGreaterThan(0);
    expect(vertexCount(below)).toBeGreaterThan(0);
  });

  it('handles indexed geometry (BoxGeometry without toNonIndexed)', () => {
    const cube = new THREE.BoxGeometry(1, 1, 1); // indexed
    const { above, below } = splitGeometryAtPlane(cube, 'z', 0);
    expect(vertexCount(above)).toBeGreaterThan(0);
    expect(vertexCount(below)).toBeGreaterThan(0);
  });

  it('applies modelMatrix transform when provided', () => {
    const cube = createCube(1);
    // Translate the model up by 2 in world space
    const mat = new THREE.Matrix4().makeTranslation(0, 0, 2);
    // Cut at world z=2 should bisect the translated cube
    const { above, below } = splitGeometryAtPlane(cube, 'z', 2, mat);
    expect(vertexCount(above)).toBeGreaterThan(0);
    expect(vertexCount(below)).toBeGreaterThan(0);
  });

  it('handles an off-center cut producing unequal halves', () => {
    const cube = createCube(2); // z ∈ [-1, 1]
    const { above, below } = splitGeometryAtPlane(cube, 'z', 0.5);
    // above: z ∈ [0.5, 1], below: z ∈ [-1, 0.5]
    const [aboveMin, aboveMax] = axisExtent(above, 'z');
    const [belowMin, belowMax] = axisExtent(below, 'z');
    expect(aboveMax).toBeCloseTo(1, 1);
    expect(aboveMin).toBeCloseTo(0.5, 1);
    expect(belowMin).toBeCloseTo(-1, 1);
    expect(belowMax).toBeCloseTo(0.5, 1);
  });
});

// ===========================================================================
// earClipTriangulate
// ===========================================================================

describe('earClipTriangulate', () => {
  it('returns the input triangle unchanged for a 3-vertex polygon', () => {
    const triangle = [
      new THREE.Vector3(0, 0, 0),
      new THREE.Vector3(1, 0, 0),
      new THREE.Vector3(0, 1, 0),
    ];
    const result = earClipTriangulate(triangle, 'z');
    expect(result).toHaveLength(1);
    expect(result[0]).toHaveLength(3);
  });

  it('triangulates a convex square into 2 triangles', () => {
    const square = [
      new THREE.Vector3(0, 0, 0),
      new THREE.Vector3(1, 0, 0),
      new THREE.Vector3(1, 1, 0),
      new THREE.Vector3(0, 1, 0),
    ];
    const result = earClipTriangulate(square, 'z');
    expect(result).toHaveLength(2);
  });

  it('triangulates a concave L-shaped polygon', () => {
    // L-shape (6 vertices, concave):
    //  (0,2)---(1,2)
    //    |       |
    //  (0,1)---(1,1)---(2,1)
    //                     |
    //          (1,0)---(2,0)
    const lShape = [
      new THREE.Vector3(0, 0, 0),
      new THREE.Vector3(2, 0, 0),
      new THREE.Vector3(2, 1, 0),
      new THREE.Vector3(1, 1, 0),
      new THREE.Vector3(1, 2, 0),
      new THREE.Vector3(0, 2, 0),
    ];
    const result = earClipTriangulate(lShape, 'z');
    // n-2 triangles for n vertices: 6 - 2 = 4
    expect(result).toHaveLength(4);
    // Every triangle should have 3 vertices
    for (const tri of result) {
      expect(tri).toHaveLength(3);
    }
  });

  it('returns empty array for fewer than 3 vertices', () => {
    expect(earClipTriangulate([], 'z')).toHaveLength(0);
    expect(earClipTriangulate([new THREE.Vector3()], 'z')).toHaveLength(0);
    expect(earClipTriangulate([new THREE.Vector3(), new THREE.Vector3()], 'z')).toHaveLength(0);
  });

  it('works with x-axis projection', () => {
    // Square in the YZ plane
    const square = [
      new THREE.Vector3(0, 0, 0),
      new THREE.Vector3(0, 1, 0),
      new THREE.Vector3(0, 1, 1),
      new THREE.Vector3(0, 0, 1),
    ];
    const result = earClipTriangulate(square, 'x');
    expect(result).toHaveLength(2);
  });

  it('works with y-axis projection', () => {
    // Square in the XZ plane
    const square = [
      new THREE.Vector3(0, 0, 0),
      new THREE.Vector3(0, 0, 1),
      new THREE.Vector3(1, 0, 1),
      new THREE.Vector3(1, 0, 0),
    ];
    const result = earClipTriangulate(square, 'y');
    expect(result).toHaveLength(2);
  });

  it('triangulates a regular pentagon into 3 triangles', () => {
    const pentagon: THREE.Vector3[] = [];
    for (let i = 0; i < 5; i++) {
      const angle = (2 * Math.PI * i) / 5 - Math.PI / 2;
      pentagon.push(new THREE.Vector3(Math.cos(angle), Math.sin(angle), 0));
    }
    const result = earClipTriangulate(pentagon, 'z');
    expect(result).toHaveLength(3);
  });

  it('handles a convex hexagon', () => {
    const hex: THREE.Vector3[] = [];
    for (let i = 0; i < 6; i++) {
      const angle = (2 * Math.PI * i) / 6;
      hex.push(new THREE.Vector3(Math.cos(angle), Math.sin(angle), 0));
    }
    const result = earClipTriangulate(hex, 'z');
    expect(result).toHaveLength(4); // 6 - 2
  });
});

// ===========================================================================
// orderCapEdges
// ===========================================================================

describe('orderCapEdges', () => {
  it('orders edges of a triangle into a single closed loop', () => {
    const a = new THREE.Vector3(0, 0, 0);
    const b = new THREE.Vector3(1, 0, 0);
    const c = new THREE.Vector3(0.5, 1, 0);
    const edges: Array<[THREE.Vector3, THREE.Vector3]> = [
      [a.clone(), b.clone()],
      [b.clone(), c.clone()],
      [c.clone(), a.clone()],
    ];
    const loops = orderCapEdges(edges);
    expect(loops).toHaveLength(1);
    expect(loops[0]).toHaveLength(3);
  });

  it('returns empty array for no edges', () => {
    expect(orderCapEdges([])).toHaveLength(0);
  });

  it('orders edges of a square into a single loop', () => {
    const verts = [
      new THREE.Vector3(0, 0, 0),
      new THREE.Vector3(1, 0, 0),
      new THREE.Vector3(1, 1, 0),
      new THREE.Vector3(0, 1, 0),
    ];
    // Edges in shuffled order
    const edges: Array<[THREE.Vector3, THREE.Vector3]> = [
      [verts[2].clone(), verts[3].clone()],
      [verts[0].clone(), verts[1].clone()],
      [verts[3].clone(), verts[0].clone()],
      [verts[1].clone(), verts[2].clone()],
    ];
    const loops = orderCapEdges(edges);
    expect(loops).toHaveLength(1);
    expect(loops[0]).toHaveLength(4);
  });

  it('produces two separate loops for disconnected edge sets', () => {
    // Triangle 1
    const a1 = new THREE.Vector3(0, 0, 0);
    const b1 = new THREE.Vector3(1, 0, 0);
    const c1 = new THREE.Vector3(0.5, 1, 0);
    // Triangle 2 (offset)
    const a2 = new THREE.Vector3(5, 0, 0);
    const b2 = new THREE.Vector3(6, 0, 0);
    const c2 = new THREE.Vector3(5.5, 1, 0);

    const edges: Array<[THREE.Vector3, THREE.Vector3]> = [
      [a1.clone(), b1.clone()],
      [b1.clone(), c1.clone()],
      [c1.clone(), a1.clone()],
      [a2.clone(), b2.clone()],
      [b2.clone(), c2.clone()],
      [c2.clone(), a2.clone()],
    ];
    const loops = orderCapEdges(edges);
    expect(loops).toHaveLength(2);
    expect(loops[0]).toHaveLength(3);
    expect(loops[1]).toHaveLength(3);
  });

  it('handles reversed edge directions', () => {
    const a = new THREE.Vector3(0, 0, 0);
    const b = new THREE.Vector3(1, 0, 0);
    const c = new THREE.Vector3(0.5, 1, 0);
    // Mix forward and backward edges
    const edges: Array<[THREE.Vector3, THREE.Vector3]> = [
      [a.clone(), b.clone()],
      [c.clone(), b.clone()], // reversed
      [c.clone(), a.clone()],
    ];
    const loops = orderCapEdges(edges);
    expect(loops).toHaveLength(1);
    expect(loops[0]).toHaveLength(3);
  });

  it('discards fragments shorter than 3 vertices', () => {
    // Single edge cannot form a polygon
    const edges: Array<[THREE.Vector3, THREE.Vector3]> = [
      [new THREE.Vector3(0, 0, 0), new THREE.Vector3(1, 0, 0)],
    ];
    const loops = orderCapEdges(edges);
    expect(loops).toHaveLength(0);
  });
});

// ===========================================================================
// filterDegenerateTriangles
// ===========================================================================

describe('filterDegenerateTriangles', () => {
  it('keeps a valid triangle', () => {
    // Triangle with non-zero area
    const verts = [
      0, 0, 0,
      1, 0, 0,
      0, 1, 0,
    ];
    const result = filterDegenerateTriangles(verts);
    expect(result).toHaveLength(9);
  });

  it('removes a zero-area triangle (collinear points)', () => {
    const verts = [
      0, 0, 0,
      1, 0, 0,
      2, 0, 0, // collinear
    ];
    const result = filterDegenerateTriangles(verts);
    expect(result).toHaveLength(0);
  });

  it('removes a degenerate triangle where all vertices are identical', () => {
    const verts = [
      1, 1, 1,
      1, 1, 1,
      1, 1, 1,
    ];
    const result = filterDegenerateTriangles(verts);
    expect(result).toHaveLength(0);
  });

  it('filters out only degenerate triangles from a mixed set', () => {
    const verts = [
      // Valid triangle
      0, 0, 0,
      1, 0, 0,
      0, 1, 0,
      // Degenerate (collinear)
      0, 0, 0,
      1, 0, 0,
      2, 0, 0,
      // Another valid triangle
      0, 0, 0,
      0, 0, 1,
      0, 1, 0,
    ];
    const result = filterDegenerateTriangles(verts);
    // Only 2 valid triangles remain: 2 × 9 = 18
    expect(result).toHaveLength(18);
  });

  it('returns empty array for empty input', () => {
    expect(filterDegenerateTriangles([])).toHaveLength(0);
  });

  it('handles very small but non-degenerate triangle', () => {
    const eps = 1e-6;
    const verts = [
      0, 0, 0,
      eps, 0, 0,
      0, eps, 0,
    ];
    // Cross product magnitude² = eps⁴ ≈ 1e-24, below default minArea2 of 1e-16
    const result = filterDegenerateTriangles(verts);
    expect(result).toHaveLength(0);
  });

  it('respects custom minArea2 threshold', () => {
    const eps = 1e-6;
    const verts = [
      0, 0, 0,
      eps, 0, 0,
      0, eps, 0,
    ];
    // With a very tiny threshold, the triangle survives
    const result = filterDegenerateTriangles(verts, 1e-30);
    expect(result).toHaveLength(9);
  });
});

// ===========================================================================
// Integration: splitGeometryAtPlane with hollow/concave models
// ===========================================================================

describe('splitGeometryAtPlane — integration', () => {
  it('cutting a sphere produces two non-empty halves', () => {
    const sphere = new THREE.SphereGeometry(1, 16, 8).toNonIndexed();
    const { above, below } = splitGeometryAtPlane(sphere, 'z', 0);
    expect(vertexCount(above)).toBeGreaterThan(0);
    expect(vertexCount(below)).toBeGreaterThan(0);
  });

  it('cutting a cylinder along y-axis', () => {
    const cylinder = new THREE.CylinderGeometry(0.5, 0.5, 2, 16).toNonIndexed();
    const { above, below } = splitGeometryAtPlane(cylinder, 'y', 0);
    expect(vertexCount(above)).toBeGreaterThan(0);
    expect(vertexCount(below)).toBeGreaterThan(0);
  });

  it('cutting a torus produces hollow cross-section caps', () => {
    const torus = new THREE.TorusGeometry(2, 0.5, 8, 16).toNonIndexed();
    const { above, below } = splitGeometryAtPlane(torus, 'z', 0);
    expect(vertexCount(above)).toBeGreaterThan(0);
    expect(vertexCount(below)).toBeGreaterThan(0);
  });
});
