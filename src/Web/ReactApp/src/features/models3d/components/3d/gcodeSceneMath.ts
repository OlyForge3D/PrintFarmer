import type { BedBounds, DetailedLayer } from '@/features/slicer/services';

export type Vec3 = [number, number, number];

/** Axis-aligned box in G-code (machine) millimetres. */
export interface GcodeBounds {
  minX: number;
  minY: number;
  minZ: number;
  maxX: number;
  maxY: number;
  maxZ: number;
}

/**
 * Euler rotation applied to the group holding all G-code geometry. Three.js is
 * Y-up while printers are Z-up; rotating -90° about X maps machine
 * (x, y, z) to scene (x, z, -y), so the bed lies on the scene floor, printer Z
 * points up, and the front of the bed (low Y) faces the default camera.
 */
export const GCODE_TO_SCENE_ROTATION: Vec3 = [-Math.PI / 2, 0, 0];

/** Same mapping as `GCODE_TO_SCENE_ROTATION`, for points computed outside the scene graph. */
export function gcodeToScene(x: number, y: number, z: number): Vec3 {
  return [x, z, -y];
}

/**
 * Bounds of the printed part. Uses extrusion points only, so purge lines are
 * included but parking/homing travel moves do not drag the camera away from
 * the model. Falls back to every point when a file has no extrusion.
 */
export function computeToolpathBounds(layers: readonly DetailedLayer[]): GcodeBounds | null {
  const extrusion = accumulateBounds(layers, true);
  return extrusion ?? accumulateBounds(layers, false);
}

function accumulateBounds(layers: readonly DetailedLayer[], extrusionOnly: boolean): GcodeBounds | null {
  let minX = Infinity;
  let minY = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  let maxZ = -Infinity;
  for (const layer of layers) {
    for (let i = 0; i < layer.count; i++) {
      if (extrusionOnly && layer.type[i] !== 1) continue;
      const x = layer.x[i];
      const y = layer.y[i];
      const z = layer.pz[i];
      if (x < minX) minX = x;
      if (x > maxX) maxX = x;
      if (y < minY) minY = y;
      if (y > maxY) maxY = y;
      if (z < minZ) minZ = z;
      if (z > maxZ) maxZ = z;
    }
  }
  if (minX === Infinity) return null;
  return { minX, minY, minZ, maxX, maxY, maxZ };
}

const FALLBACK_BED_SIZE_MM = 200;
const FALLBACK_BED_MARGIN_MM = 10;

/**
 * Bed rectangle to draw under the model: the file's declared printable area
 * when known, otherwise a 10 mm-aligned area that covers the toolpath (and the
 * machine origin) with a margin.
 */
export function resolveBedBounds(bed: BedBounds | null | undefined, toolpath: GcodeBounds | null): BedBounds {
  if (bed) return bed;
  if (!toolpath) return { minX: 0, minY: 0, maxX: FALLBACK_BED_SIZE_MM, maxY: FALLBACK_BED_SIZE_MM };
  const snapDown = (v: number) => Math.floor(v / FALLBACK_BED_MARGIN_MM) * FALLBACK_BED_MARGIN_MM;
  const snapUp = (v: number) => Math.ceil(v / FALLBACK_BED_MARGIN_MM) * FALLBACK_BED_MARGIN_MM;
  return {
    minX: snapDown(Math.min(0, toolpath.minX - FALLBACK_BED_MARGIN_MM)),
    minY: snapDown(Math.min(0, toolpath.minY - FALLBACK_BED_MARGIN_MM)),
    maxX: snapUp(toolpath.maxX + FALLBACK_BED_MARGIN_MM),
    maxY: snapUp(toolpath.maxY + FALLBACK_BED_MARGIN_MM),
  };
}

/** Grid line segments (G-code coordinates, Z = 0) covering the bed, including its outline. */
export function buildBedGridPositions(bed: BedBounds, cellMm = 10): Float32Array {
  const xs = gridStops(bed.minX, bed.maxX, cellMm);
  const ys = gridStops(bed.minY, bed.maxY, cellMm);
  const positions = new Float32Array((xs.length + ys.length) * 6);
  let o = 0;
  for (const x of xs) {
    positions.set([x, bed.minY, 0, x, bed.maxY, 0], o);
    o += 6;
  }
  for (const y of ys) {
    positions.set([bed.minX, y, 0, bed.maxX, y, 0], o);
    o += 6;
  }
  return positions;
}

function gridStops(min: number, max: number, cell: number): number[] {
  const stops = [min];
  for (let v = Math.floor(min / cell) * cell + cell; v < max - 1e-6; v += cell) {
    if (v > min + 1e-6) stops.push(v);
  }
  stops.push(max);
  return stops;
}

export interface CameraFit {
  position: Vec3;
  target: Vec3;
  near: number;
  far: number;
}

// Front-right, above the bed (scene axes: +X right, +Y up, +Z toward viewer).
const VIEW_DIRECTION: Vec3 = normalize([0.6, 0.75, 1]);
const FIT_MARGIN = 1.15;

/**
 * Perspective camera placement that keeps the whole toolpath box in view.
 * The box's bounding sphere must fit inside the narrower of the vertical and
 * horizontal fields of view, so the model is never cropped on tall or
 * narrow canvases.
 */
export function computeCameraFit(bounds: GcodeBounds, fovDeg: number, aspect: number): CameraFit {
  const center = gcodeToScene(
    (bounds.minX + bounds.maxX) / 2,
    (bounds.minY + bounds.maxY) / 2,
    (bounds.minZ + bounds.maxZ) / 2,
  );
  const radius = Math.max(
    1,
    Math.hypot(bounds.maxX - bounds.minX, bounds.maxY - bounds.minY, bounds.maxZ - bounds.minZ) / 2,
  );
  const halfV = (fovDeg * Math.PI) / 360;
  const halfH = Math.atan(Math.tan(halfV) * Math.max(aspect, 1e-3));
  const distance = (radius / Math.sin(Math.min(halfV, halfH))) * FIT_MARGIN;

  return {
    target: center,
    position: [
      center[0] + VIEW_DIRECTION[0] * distance,
      center[1] + VIEW_DIRECTION[1] * distance,
      center[2] + VIEW_DIRECTION[2] * distance,
    ],
    near: Math.max(0.1, distance / 1000),
    far: distance * 20,
  };
}

function normalize(v: Vec3): Vec3 {
  const length = Math.hypot(v[0], v[1], v[2]);
  return [v[0] / length, v[1] / length, v[2] / length];
}
