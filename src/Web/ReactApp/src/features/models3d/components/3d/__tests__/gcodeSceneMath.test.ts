import { describe, it, expect } from 'vitest';
import * as THREE from 'three';
import type { DetailedLayer } from '@/features/slicer/services';
import {
  GCODE_TO_SCENE_ROTATION,
  buildBedGridPositions,
  computeCameraFit,
  computeFitDistance,
  computeToolpathBounds,
  gcodeToScene,
  resolveBedBounds,
  type GcodeBounds,
} from '../gcodeSceneMath';

function layer(points: Array<[number, number, number, 0 | 1]>): DetailedLayer {
  return {
    index: 0,
    z: points[0]?.[2] ?? 0,
    count: points.length,
    x: Float32Array.from(points.map(p => p[0])),
    y: Float32Array.from(points.map(p => p[1])),
    pz: Float32Array.from(points.map(p => p[2])),
    e: new Float32Array(points.length),
    feedRate: new Float32Array(points.length),
    type: Uint8Array.from(points.map(p => p[3])),
    tool: new Int32Array(points.length),
  };
}

function toScene(x: number, y: number, z: number): THREE.Vector3 {
  return new THREE.Vector3(x, y, z).applyEuler(new THREE.Euler(...GCODE_TO_SCENE_ROTATION));
}

describe('G-code to scene mapping', () => {
  it.each([
    [10, 20, 0.2],
    [-5, 100, 30],
    [256, 0, 0],
  ])('gcodeToScene(%d, %d, %d) matches the group rotation', (x, y, z) => {
    const expected = toScene(x, y, z);
    const [sx, sy, sz] = gcodeToScene(x, y, z);
    expect(sx).toBeCloseTo(expected.x, 6);
    expect(sy).toBeCloseTo(expected.y, 6);
    expect(sz).toBeCloseTo(expected.z, 6);
  });

  it('puts printer Z on the scene up axis and the bed on the floor', () => {
    expect(toScene(0, 0, 1).y).toBeCloseTo(1, 6);
    // First layer (Z = 0.2) sits just above the bed plane (scene y = 0).
    expect(toScene(120, 80, 0.2).y).toBeCloseTo(0.2, 6);
    expect(toScene(120, 80, 0).y).toBeCloseTo(0, 6);
    // Bed X stays horizontal; bed Y runs along the scene depth axis.
    expect(toScene(1, 0, 0).toArray().map(v => Math.round(v * 1e6) / 1e6)).toEqual([1, 0, 0]);
    expect(Math.abs(toScene(0, 1, 0).z)).toBeCloseTo(1, 6);
    expect(toScene(0, 1, 0).y).toBeCloseTo(0, 6);
  });
});

describe('computeToolpathBounds', () => {
  it('uses extrusion points only so travel/park moves do not widen the box', () => {
    const bounds = computeToolpathBounds([
      layer([[50, 60, 0.2, 1], [70, 80, 0.2, 1], [0, 250, 0.2, 0]]),
      layer([[60, 70, 12, 1], [300, 300, 40, 0]]),
    ]);
    expect(bounds).toEqual({ minX: 50, minY: 60, minZ: Math.fround(0.2), maxX: 70, maxY: 80, maxZ: 12 });
  });

  it('falls back to all points when nothing is extruded, and null when empty', () => {
    expect(computeToolpathBounds([layer([[1, 2, 3, 0], [4, 5, 6, 0]])]))
      .toEqual({ minX: 1, minY: 2, minZ: 3, maxX: 4, maxY: 5, maxZ: 6 });
    expect(computeToolpathBounds([])).toBeNull();
  });
});

describe('resolveBedBounds', () => {
  it('prefers the declared bed', () => {
    const bed = { minX: 0, minY: 0, maxX: 256, maxY: 256 };
    expect(resolveBedBounds(bed, { minX: 1, minY: 1, minZ: 0, maxX: 2, maxY: 2, maxZ: 1 })).toBe(bed);
  });

  it('falls back to a 10 mm-aligned area covering origin and toolpath with margin', () => {
    expect(resolveBedBounds(null, { minX: 95, minY: 103, minZ: 0.2, maxX: 161, maxY: 157, maxZ: 20 }))
      .toEqual({ minX: 0, minY: 0, maxX: 180, maxY: 170 });
  });

  it('uses a default 200 mm bed when there is no toolpath', () => {
    expect(resolveBedBounds(undefined, null)).toEqual({ minX: 0, minY: 0, maxX: 200, maxY: 200 });
  });
});

describe('buildBedGridPositions', () => {
  it('emits one segment per grid line including the outline, all at Z = 0', () => {
    const positions = buildBedGridPositions({ minX: 0, minY: 0, maxX: 30, maxY: 20 });
    // x lines at 0,10,20,30 (4) + y lines at 0,10,20 (3) -> 7 segments of 6 floats.
    expect(positions.length).toBe(7 * 6);
    for (let i = 2; i < positions.length; i += 3) expect(positions[i]).toBe(0);
  });

  it('includes non-aligned bed edges', () => {
    const positions = buildBedGridPositions({ minX: -2, minY: -4, maxX: 252, maxY: 212 });
    const xs = new Set<number>();
    for (let i = 0; i < positions.length; i += 6) if (positions[i] === positions[i + 3]) xs.add(positions[i]);
    expect(xs.has(-2)).toBe(true);
    expect(xs.has(252)).toBe(true);
  });
});

describe('computeCameraFit', () => {
  const boxes: GcodeBounds[] = [
    { minX: 95, minY: 100, minZ: 0.2, maxX: 160, maxY: 155, maxZ: 40 },
    { minX: 10, minY: 10, minZ: 0.2, maxX: 240, maxY: 240, maxZ: 2 },
    { minX: 120, minY: 120, minZ: 0.2, maxX: 130, maxY: 130, maxZ: 180 },
  ];

  it.each(boxes.flatMap(box => [0.5, 1, 2].map(aspect => [box, aspect] as const)))(
    'keeps every corner of %o in view at aspect %d',
    (box, aspect) => {
      const fov = 45;
      const fit = computeCameraFit(box, fov, aspect);
      const camera = new THREE.PerspectiveCamera(fov, aspect, fit.near, fit.far);
      camera.position.set(...fit.position);
      camera.lookAt(...fit.target);
      camera.updateMatrixWorld();
      camera.updateProjectionMatrix();

      for (const x of [box.minX, box.maxX]) {
        for (const y of [box.minY, box.maxY]) {
          for (const z of [box.minZ, box.maxZ]) {
            const ndc = toScene(x, y, z).project(camera);
            expect(Math.abs(ndc.x)).toBeLessThanOrEqual(1);
            expect(Math.abs(ndc.y)).toBeLessThanOrEqual(1);
            expect(ndc.z).toBeGreaterThan(-1);
            expect(ndc.z).toBeLessThan(1);
          }
        }
      }
    },
  );

  it('targets the toolpath centre from above the bed', () => {
    const box = boxes[0];
    const fit = computeCameraFit(box, 45, 1.5);
    const centre = toScene((box.minX + box.maxX) / 2, (box.minY + box.maxY) / 2, (box.minZ + box.maxZ) / 2);
    expect(fit.target[0]).toBeCloseTo(centre.x, 6);
    expect(fit.target[1]).toBeCloseTo(centre.y, 6);
    expect(fit.target[2]).toBeCloseTo(centre.z, 6);
    expect(fit.position[1]).toBeGreaterThan(fit.target[1]);
  });

  it.each([[2, 0.4], [0.4, 2], [1.6, 0.46]])(
    'keeps every corner in view after rescaling a fit from aspect %d to %d',
    (from, to) => {
      const box = boxes[0];
      const fov = 45;
      const fit = computeCameraFit(box, fov, from);
      const scale = computeFitDistance(box, fov, to) / computeFitDistance(box, fov, from);
      const target = new THREE.Vector3(...fit.target);
      const position = new THREE.Vector3(...fit.position).sub(target).multiplyScalar(scale).add(target);
      const camera = new THREE.PerspectiveCamera(fov, to, fit.near * scale, fit.far * scale);
      camera.position.copy(position);
      camera.lookAt(target);
      camera.updateMatrixWorld();
      camera.updateProjectionMatrix();

      for (const x of [box.minX, box.maxX]) {
        for (const y of [box.minY, box.maxY]) {
          for (const z of [box.minZ, box.maxZ]) {
            const ndc = toScene(x, y, z).project(camera);
            expect(Math.abs(ndc.x)).toBeLessThanOrEqual(1);
            expect(Math.abs(ndc.y)).toBeLessThanOrEqual(1);
          }
        }
      }
    },
  );

  it('needs more distance for a narrower canvas', () => {
    const box = boxes[0];
    expect(computeFitDistance(box, 45, 0.46)).toBeGreaterThan(computeFitDistance(box, 45, 1.6));
  });
});
