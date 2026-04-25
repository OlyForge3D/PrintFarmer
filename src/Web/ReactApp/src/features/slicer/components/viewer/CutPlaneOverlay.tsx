/**
 * Cut Plane Overlay for plane-based model splitting with multi-axis support.
 * Matches OrcaSlicer's cut control UI and functionality.
 */
import { useRef, useState, useEffect, useCallback, useMemo } from 'react';
import { useThree, useFrame } from '@react-three/fiber';
import { Html } from '@react-three/drei';
import * as THREE from 'three';
import { Button, Input, Checkbox } from '@/common/components/ui';
import type { BedConfig } from './SlicerBedVisualization';

export interface CutOptions {
  /** Cut axis. "Upper" maps to +axis side, "Lower" maps to -axis side. */
  cutAxis: 'x' | 'y' | 'z';
  /** World-space plane position along cutAxis. */
  worldPlanePos: number;
  keepUpper: boolean;
  keepLower: boolean;
  placeOnCutUpper: boolean;
  placeOnCutLower: boolean;
  flipUpper: boolean;
  flipLower: boolean;
  cutToParts: boolean;
}

interface CutPlaneOverlayProps {
  /** Reference to the selected model's Object3D */
  meshRef: React.RefObject<THREE.Object3D | null>;
  /** Whether cut mode is active */
  active: boolean;
  /** Bed configuration */
  bedConfig: BedConfig;
  /** Called when the cut is confirmed with two new geometries */
  onCutComplete: (
    geometryAbove: THREE.BufferGeometry,
    geometryBelow: THREE.BufferGeometry,
    options?: CutOptions
  ) => void;
  /** Called when cut is cancelled */
  onCutCancel: () => void;
}

type CutAxis = 'x' | 'y' | 'z';

/** Linearly interpolate between two 3D points */
function lerpVertex(a: THREE.Vector3, b: THREE.Vector3, t: number): THREE.Vector3 {
  return new THREE.Vector3(
    a.x + (b.x - a.x) * t,
    a.y + (b.y - a.y) * t,
    a.z + (b.z - a.z) * t,
  );
}

/**
 * Order cap edges into one or more closed polygon loops by matching endpoints.
 * A planar cross-section of a non-convex / multi-region mesh may produce several
 * disjoint loops; each is returned as a separate polygon (without repeating the
 * first vertex at the end).
 */
function orderCapEdges(
  edges: Array<[THREE.Vector3, THREE.Vector3]>,
  epsilon = 1e-5,
): THREE.Vector3[][] {
  if (edges.length === 0) return [];
  const eq = (a: THREE.Vector3, b: THREE.Vector3) => a.distanceTo(b) < epsilon;

  const loops: THREE.Vector3[][] = [];
  const used = new Set<number>();

  while (used.size < edges.length) {
    // Pick the next unused edge as the seed for a new loop
    let seed = -1;
    for (let i = 0; i < edges.length; i++) {
      if (!used.has(i)) { seed = i; break; }
    }
    if (seed === -1) break;

    const polygon: THREE.Vector3[] = [edges[seed][0], edges[seed][1]];
    used.add(seed);

    // Extend the polygon by chaining edges that share an endpoint with `last`
    while (used.size < edges.length) {
      const last = polygon[polygon.length - 1];
      let found = false;
      for (let i = 0; i < edges.length; i++) {
        if (used.has(i)) continue;
        if (eq(edges[i][0], last)) {
          polygon.push(edges[i][1]);
          used.add(i);
          found = true;
          break;
        }
        if (eq(edges[i][1], last)) {
          polygon.push(edges[i][0]);
          used.add(i);
          found = true;
          break;
        }
      }
      if (!found) break;
      // Loop closed back to start?
      if (eq(polygon[0], polygon[polygon.length - 1])) {
        polygon.pop();
        break;
      }
    }

    if (polygon.length >= 3) loops.push(polygon);
  }

  return loops;
}

/** Drop adjacent duplicate and collinear vertices from a polygon (in-place safe). */
function removeCollinearVertices(
  polygon: THREE.Vector3[],
  project: (v: THREE.Vector3) => [number, number],
  epsilon = 1e-9,
): THREE.Vector3[] {
  if (polygon.length < 3) return polygon;
  const out: THREE.Vector3[] = [];
  const n = polygon.length;
  for (let i = 0; i < n; i++) {
    const prev = polygon[(i + n - 1) % n];
    const curr = polygon[i];
    const next = polygon[(i + 1) % n];
    const [px, py] = project(prev);
    const [cx, cy] = project(curr);
    const [nx, ny] = project(next);
    // Drop exact duplicates of the previous vertex
    if (Math.abs(cx - px) < epsilon && Math.abs(cy - py) < epsilon) continue;
    // Drop collinear vertices (cross product near zero)
    const cross = (cx - px) * (ny - py) - (cy - py) * (nx - px);
    if (Math.abs(cross) < epsilon) continue;
    out.push(curr);
  }
  return out.length >= 3 ? out : polygon;
}

/** Check if point (px,py) is inside triangle (x1,y1)-(x2,y2)-(x3,y3). */
function pointInTriangle2D(
  px: number, py: number,
  x1: number, y1: number,
  x2: number, y2: number,
  x3: number, y3: number,
): boolean {
  const d1 = (px - x3) * (y1 - y3) - (x1 - x3) * (py - y3);
  const d2 = (px - x1) * (y2 - y1) - (x2 - x1) * (py - y1);
  const d3 = (px - x2) * (y3 - y2) - (x3 - x2) * (py - y2);
  return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
}

/**
 * Triangulate a polygon using ear-clipping. Projects to 2D such that CCW winding
 * in the projection corresponds to a +cutAxis triangle normal in 3D. Returns the
 * triangles preserving original 3D vertices. Robust to collinear vertices and
 * uses a quadratic safety bound (worst-case ear-clipping is O(n²)).
 */
function earClipTriangulate(
  polygon: THREE.Vector3[],
  axis: CutAxis,
): Array<[THREE.Vector3, THREE.Vector3, THREE.Vector3]> {
  if (polygon.length < 3) return [];
  if (polygon.length === 3) return [[polygon[0], polygon[1], polygon[2]]];

  // Project so that 2D CCW winding ↔ +cutAxis 3D normal.
  // axis='x': (y,z) → y×z = +x ✓
  // axis='y': (z,x) → z×x = +y ✓
  // axis='z': (x,y) → x×y = +z ✓
  const project = (v: THREE.Vector3): [number, number] => {
    if (axis === 'x') return [v.y, v.z];
    if (axis === 'y') return [v.z, v.x];
    return [v.x, v.y];
  };

  // Pre-pass: drop adjacent duplicates and collinear vertices.
  let work = removeCollinearVertices(polygon, project);
  if (work.length < 3) return [];
  if (work.length === 3) return [[work[0], work[1], work[2]]];

  // Compute signed area (shoelace, conventional sign: > 0 means CCW).
  let signedArea = 0;
  for (let i = 0; i < work.length; i++) {
    const [x1, y1] = project(work[i]);
    const [x2, y2] = project(work[(i + 1) % work.length]);
    signedArea += x1 * y2 - x2 * y1;
  }

  // Force CCW orientation so emitted triangles have +cutAxis normal.
  if (signedArea < 0) work = work.slice().reverse();

  const indices = Array.from({ length: work.length }, (_, i) => i);
  const triangles: Array<[THREE.Vector3, THREE.Vector3, THREE.Vector3]> = [];

  // O(n²) worst-case bound on failed-ear iterations.
  let safety = indices.length * indices.length + 4;
  while (indices.length > 3) {
    let earFound = false;
    for (let i = 0; i < indices.length; i++) {
      const prevIdx = indices[(i + indices.length - 1) % indices.length];
      const currIdx = indices[i];
      const nextIdx = indices[(i + 1) % indices.length];

      const [px, py] = project(work[prevIdx]);
      const [cx, cy] = project(work[currIdx]);
      const [nx, ny] = project(work[nextIdx]);

      // Convex test in CCW orientation. Treat collinear (cross == 0) as non-ear
      // to avoid emitting zero-area triangles; collinear vertices were already
      // pruned but float noise may leave near-zero values.
      const cross = (cx - px) * (ny - py) - (cy - py) * (nx - px);
      if (cross <= 1e-12) continue;

      let containsPoint = false;
      for (const idx of indices) {
        if (idx === prevIdx || idx === currIdx || idx === nextIdx) continue;
        const [tx, ty] = project(work[idx]);
        if (pointInTriangle2D(tx, ty, px, py, cx, cy, nx, ny)) {
          containsPoint = true;
          break;
        }
      }
      if (containsPoint) continue;

      triangles.push([work[prevIdx], work[currIdx], work[nextIdx]]);
      indices.splice(i, 1);
      earFound = true;
      break;
    }
    if (!earFound) {
      if (--safety <= 0) {
        // Fallback fan from indices[0] over remaining indices. This is not ideal
        // for non-convex remainders, but is preferable to silently dropping the
        // cap. Surfaces it via console for diagnostics.
        // eslint-disable-next-line no-console
        console.warn('earClipTriangulate: fallback fan triangulation', {
          remaining: indices.length,
          axis,
        });
        for (let i = 1; i < indices.length - 1; i++) {
          triangles.push([work[indices[0]], work[indices[i]], work[indices[i + 1]]]);
        }
        return triangles;
      }
    }
  }

  if (indices.length === 3) {
    triangles.push([work[indices[0]], work[indices[1]], work[indices[2]]]);
  }
  return triangles;
}

/** Filter out degenerate (near-zero-area) triangles from a flat vertex array. */
function filterDegenerateTriangles(verts: number[], minArea2 = 1e-16): number[] {
  const result: number[] = [];
  for (let i = 0; i < verts.length; i += 9) {
    const abx = verts[i + 3] - verts[i],     aby = verts[i + 4] - verts[i + 1], abz = verts[i + 5] - verts[i + 2];
    const acx = verts[i + 6] - verts[i],     acy = verts[i + 7] - verts[i + 1], acz = verts[i + 8] - verts[i + 2];
    const nx = aby * acz - abz * acy;
    const ny = abz * acx - abx * acz;
    const nz = abx * acy - aby * acx;
    if (nx * nx + ny * ny + nz * nz > minArea2) {
      result.push(
        verts[i], verts[i + 1], verts[i + 2],
        verts[i + 3], verts[i + 4], verts[i + 5],
        verts[i + 6], verts[i + 7], verts[i + 8],
      );
    }
  }
  return result;
}

/**
 * Split geometry along a world-space plane. Caller specifies the plane axis
 * (which determines the +/- side meaning) and a point on the plane in world
 * space. The plane is transformed into model-local space via the inverse of
 * `modelMatrix`, supporting rotated/scaled parents correctly.
 *
 * "above" = vertices on the +cutAxis side of the plane in world space.
 * "below" = vertices on the -cutAxis side.
 */
function splitGeometryAtPlane(
  geometry: THREE.BufferGeometry,
  axis: CutAxis,
  worldPlanePos: number,
  modelMatrix?: THREE.Matrix4,
): { above: THREE.BufferGeometry; below: THREE.BufferGeometry } {
  // Build the world-space plane as a THREE.Plane (normal + constant).
  const worldNormal = axis === 'x'
    ? new THREE.Vector3(1, 0, 0)
    : axis === 'y'
    ? new THREE.Vector3(0, 1, 0)
    : new THREE.Vector3(0, 0, 1);
  const worldPlane = new THREE.Plane(worldNormal.clone(), -worldPlanePos);

  // Transform the world plane into local space. THREE.Plane.applyMatrix4 with the
  // inverse model matrix yields the equivalent local plane (correctly handles
  // rotation; for non-uniform scale it will renormalize the normal).
  const localPlane = worldPlane.clone();
  if (modelMatrix) {
    const inv = modelMatrix.clone().invert();
    localPlane.applyMatrix4(inv);
  }

  const posAttr = geometry.getAttribute('position');
  const index = geometry.getIndex();
  const triCount = index ? index.count / 3 : posAttr.count / 3;

  const aboveVerts: number[] = [];
  const belowVerts: number[] = [];
  const capEdges: Array<[THREE.Vector3, THREE.Vector3]> = [];

  const getVertex = (i: number): THREE.Vector3 => new THREE.Vector3(
    posAttr.getX(i),
    posAttr.getY(i),
    posAttr.getZ(i),
  );

  // Signed distance from local plane. >0 = above (+cutAxis side), <0 = below.
  const signedDist = (v: THREE.Vector3): number => localPlane.distanceToPoint(v);
  const classify = (d: number, eps = 1e-6): -1 | 0 | 1 =>
    d > eps ? 1 : d < -eps ? -1 : 0;

  // Project a vertex onto the local plane along the edge a→b using signed
  // distances. Used to compute cap-edge intersection points.
  const intersectOnPlane = (a: THREE.Vector3, b: THREE.Vector3, dA: number, dB: number): THREE.Vector3 => {
    const denom = dA - dB;
    // m1: guard against zero denominator (both endpoints on the plane).
    if (Math.abs(denom) < 1e-12) return a.clone();
    const t = Math.max(0, Math.min(1, dA / denom));
    return lerpVertex(a, b, t);
  };

  const pushTriVerts = (arr: number[], a: THREE.Vector3, b: THREE.Vector3, c: THREE.Vector3) => {
    arr.push(a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z);
  };

  for (let t = 0; t < triCount; t++) {
    const i0 = index ? index.getX(t * 3) : t * 3;
    const i1 = index ? index.getX(t * 3 + 1) : t * 3 + 1;
    const i2 = index ? index.getX(t * 3 + 2) : t * 3 + 2;

    const v0 = getVertex(i0);
    const v1 = getVertex(i1);
    const v2 = getVertex(i2);

    const d0 = signedDist(v0);
    const d1 = signedDist(v1);
    const d2 = signedDist(v2);

    const c0 = classify(d0);
    const c1 = classify(d1);
    const c2 = classify(d2);

    // All above
    if (c0 >= 0 && c1 >= 0 && c2 >= 0) {
      pushTriVerts(aboveVerts, v0, v1, v2);
      continue;
    }
    // All below
    if (c0 <= 0 && c1 <= 0 && c2 <= 0) {
      pushTriVerts(belowVerts, v0, v1, v2);
      continue;
    }

    // Triangle intersects the plane. Find the lone vertex (the one whose sign
    // differs from the other two) so we can split the triangle into one above-
    // side fragment and two below-side fragments (or vice versa).
    const verts = [v0, v1, v2];
    const dists = [d0, d1, d2];
    const classes = [c0, c1, c2];

    let loneIdx = -1;
    for (let i = 0; i < 3; i++) {
      const ci = classes[i];
      const cj = classes[(i + 1) % 3];
      const ck = classes[(i + 2) % 3];
      if (ci !== 0 && ((ci > 0 && cj <= 0 && ck <= 0) || (ci < 0 && cj >= 0 && ck >= 0))) {
        loneIdx = i;
        break;
      }
    }

    if (loneIdx === -1) {
      // Triangle straddles via a degenerate case (e.g. one vertex exactly on
      // the plane). Assign by majority sign.
      if (d0 + d1 + d2 > 0) pushTriVerts(aboveVerts, v0, v1, v2);
      else pushTriVerts(belowVerts, v0, v1, v2);
      continue;
    }

    const vA = verts[loneIdx];
    const vB = verts[(loneIdx + 1) % 3];
    const vC = verts[(loneIdx + 2) % 3];
    const dA = dists[loneIdx];
    const dB = dists[(loneIdx + 1) % 3];
    const dC = dists[(loneIdx + 2) % 3];
    const cA = classes[loneIdx];

    const pAB = intersectOnPlane(vA, vB, dA, dB);
    const pAC = intersectOnPlane(vA, vC, dA, dC);

    capEdges.push([pAB.clone(), pAC.clone()]);

    if (cA > 0) {
      pushTriVerts(aboveVerts, vA, pAB, pAC);
      pushTriVerts(belowVerts, pAB, vB, vC);
      pushTriVerts(belowVerts, pAB, vC, pAC);
    } else {
      pushTriVerts(belowVerts, vA, pAB, pAC);
      pushTriVerts(aboveVerts, pAB, vB, vC);
      pushTriVerts(aboveVerts, pAB, vC, pAC);
    }
  }

  // Build cap faces. Walk every closed loop in capEdges (a non-convex cut may
  // produce multiple disjoint loops). earClipTriangulate emits triangles with
  // +cutAxis-aligned normals, so the below-cap uses (a,b,c) and the above-cap
  // reverses the winding to produce -cutAxis-aligned normals.
  if (capEdges.length > 0) {
    // For non-Z cuts, the local-plane normal in local space differs from
    // worldNormal once the model is rotated; but our cap edges are in local
    // coordinates, so we triangulate against the *local* plane orientation.
    // The CutAxis-projected ear-clip uses world-axis projection though, so we
    // approximate: triangulate using the major axis of localPlane.normal.
    const ln = localPlane.normal;
    const localAxis: CutAxis =
      Math.abs(ln.x) >= Math.abs(ln.y) && Math.abs(ln.x) >= Math.abs(ln.z) ? 'x'
        : Math.abs(ln.y) >= Math.abs(ln.z) ? 'y'
        : 'z';
    const loops = orderCapEdges(capEdges);
    for (const polygon of loops) {
      if (polygon.length < 3) continue;
      const tris = earClipTriangulate(polygon, localAxis);
      for (const [a, b, c] of tris) {
        // Below-cap normal points in +localAxis direction (toward removed top).
        // Above-cap reverses.
        pushTriVerts(belowVerts, a, b, c);
        pushTriVerts(aboveVerts, c, b, a);
      }
    }
  }

  const makeGeo = (verts: number[]): THREE.BufferGeometry => {
    const filtered = filterDegenerateTriangles(verts);
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(filtered, 3));
    geo.computeVertexNormals();
    geo.computeBoundingBox();
    geo.computeBoundingSphere();
    return geo;
  };

  return {
    above: makeGeo(aboveVerts),
    below: makeGeo(belowVerts),
  };
}

export function CutPlaneOverlay({
  meshRef,
  active,
  bedConfig,
  onCutComplete,
  onCutCancel,
}: CutPlaneOverlayProps) {
  const { gl, camera, invalidate } = useThree();
  const planeRef = useRef<THREE.Mesh>(null);
  const handleRef = useRef<THREE.Mesh>(null);
  const [cutAxis, setCutAxis] = useState<CutAxis>('z');
  // n1: per-axis cut height so toggling axes preserves typed positions.
  const [cutHeights, setCutHeights] = useState<Record<CutAxis, number>>({ x: 0.5, y: 0.5, z: 0.5 });
  const cutHeight = cutHeights[cutAxis];
  const setCutHeight = useCallback(
    (h: number | ((prev: number) => number)) => {
      setCutHeights(prev => ({
        ...prev,
        [cutAxis]: typeof h === 'function' ? (h as (p: number) => number)(prev[cutAxis]) : h,
      }));
    },
    [cutAxis],
  );
  const [keepUpper, setKeepUpper] = useState(true);
  const [keepLower, setKeepLower] = useState(true);
  const [placeOnCutUpper, setPlaceOnCutUpper] = useState(true);
  const [placeOnCutLower, setPlaceOnCutLower] = useState(false);
  const [flipUpper, setFlipUpper] = useState(false);
  const [flipLower, setFlipLower] = useState(false);
  const [cutToParts, setCutToParts] = useState(false);
  const isDraggingRef = useRef(false);
  const raycaster = useMemo(() => new THREE.Raycaster(), []);

  // Compute model world-space bounds along the cut axis. Walks all vertices
  // through matrixWorld so rotated/scaled parents produce a correct world-axis
  // cut range. (M4: the cut plane is a world-axis plane, so bounds must be in
  // world coordinates too.)
  const modelBounds = useMemo(() => {
    const obj = meshRef.current;
    const geo: THREE.BufferGeometry | undefined = obj?.userData.geometry;
    if (!geo || !obj) return { min: 0, max: 10 };
    obj.updateMatrixWorld();
    const pos = geo.getAttribute('position');
    if (!pos) return { min: 0, max: 10 };
    const v = new THREE.Vector3();
    let min = Infinity;
    let max = -Infinity;
    for (let i = 0; i < pos.count; i++) {
      v.fromBufferAttribute(pos, i).applyMatrix4(obj.matrixWorld);
      const value = cutAxis === 'x' ? v.x : cutAxis === 'y' ? v.y : v.z;
      if (value < min) min = value;
      if (value > max) max = value;
    }
    if (!isFinite(min) || !isFinite(max) || min === max) return { min: 0, max: 10 };
    return { min, max };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [meshRef.current, active, cutAxis]);

  // World-space position of the cut plane along cutAxis.
  const actualPlanePos = modelBounds.min + cutHeight * (modelBounds.max - modelBounds.min);

  // Plane size: largest world-space extent of the model.
  const planeSize = useMemo(() => {
    const obj = meshRef.current;
    const geo: THREE.BufferGeometry | undefined = obj?.userData.geometry;
    if (!geo || !obj) return 100;
    obj.updateMatrixWorld();
    geo.computeBoundingBox();
    const bb = geo.boundingBox;
    if (!bb) return 100;
    // Sample bounding-box corners through matrixWorld to estimate world extent.
    const corners = [
      new THREE.Vector3(bb.min.x, bb.min.y, bb.min.z),
      new THREE.Vector3(bb.max.x, bb.min.y, bb.min.z),
      new THREE.Vector3(bb.min.x, bb.max.y, bb.min.z),
      new THREE.Vector3(bb.max.x, bb.max.y, bb.min.z),
      new THREE.Vector3(bb.min.x, bb.min.y, bb.max.z),
      new THREE.Vector3(bb.max.x, bb.min.y, bb.max.z),
      new THREE.Vector3(bb.min.x, bb.max.y, bb.max.z),
      new THREE.Vector3(bb.max.x, bb.max.y, bb.max.z),
    ];
    const wMin = new THREE.Vector3(Infinity, Infinity, Infinity);
    const wMax = new THREE.Vector3(-Infinity, -Infinity, -Infinity);
    for (const c of corners) {
      c.applyMatrix4(obj.matrixWorld);
      wMin.min(c);
      wMax.max(c);
    }
    return Math.max(wMax.x - wMin.x, wMax.y - wMin.y, wMax.z - wMin.z) * 1.4;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [meshRef.current, active]);

  // Drag to move plane
  useEffect(() => {
    if (!active) return;
    const el = gl.domElement;

    const onPointerDown = (e: PointerEvent) => {
      if (e.button !== 0) return;
      const rect = el.getBoundingClientRect();
      const ndc = new THREE.Vector2(
        ((e.clientX - rect.left) / rect.width) * 2 - 1,
        -((e.clientY - rect.top) / rect.height) * 2 + 1,
      );
      raycaster.setFromCamera(ndc, camera);
      if (handleRef.current) {
        const hits = raycaster.intersectObject(handleRef.current, false);
        if (hits.length > 0) {
          isDraggingRef.current = true;
          el.style.cursor = cutAxis === 'z' ? 'ns-resize' : 'ew-resize';
          e.preventDefault();
          e.stopPropagation();
        }
      }
    };

    const onPointerMove = (e: PointerEvent) => {
      if (!isDraggingRef.current) return;
      const rect = el.getBoundingClientRect();
      const ndc = new THREE.Vector2(
        ((e.clientX - rect.left) / rect.width) * 2 - 1,
        -((e.clientY - rect.top) / rect.height) * 2 + 1,
      );
      // Map mouse movement to normalized height based on axis
      const normalized = cutAxis === 'z'
        ? Math.max(0.02, Math.min(0.98, (ndc.y + 1) / 2))
        : Math.max(0.02, Math.min(0.98, (ndc.x + 1) / 2));
      setCutHeight(normalized);
      invalidate();
    };

    const onPointerUp = () => {
      if (isDraggingRef.current) {
        isDraggingRef.current = false;
        el.style.cursor = '';
      }
    };

    el.addEventListener('pointerdown', onPointerDown);
    el.addEventListener('pointermove', onPointerMove);
    el.addEventListener('pointerup', onPointerUp);

    return () => {
      el.removeEventListener('pointerdown', onPointerDown);
      el.removeEventListener('pointermove', onPointerMove);
      el.removeEventListener('pointerup', onPointerUp);
      isDraggingRef.current = false;
      el.style.cursor = '';
    };
  }, [active, camera, cutAxis, gl.domElement, invalidate, raycaster]);

  // Escape to cancel
  useEffect(() => {
    if (!active) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onCutCancel();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [active, onCutCancel]);

  // Confirm cut handler
  const handleConfirm = useCallback(() => {
    const obj = meshRef.current;
    const geo: THREE.BufferGeometry | undefined = obj?.userData.geometry;
    if (!geo || !obj) return;

    obj.updateMatrixWorld();
    // actualPlanePos is already a world-space coordinate.
    const worldPlanePos = actualPlanePos;
    const { above, below } = splitGeometryAtPlane(geo, cutAxis, worldPlanePos, obj.matrixWorld);

    const aboveCount = above.getAttribute('position')?.count ?? 0;
    const belowCount = below.getAttribute('position')?.count ?? 0;
    if (aboveCount < 3 || belowCount < 3) {
      onCutCancel();
      return;
    }

    onCutComplete(above, below, {
      cutAxis,
      worldPlanePos,
      keepUpper,
      keepLower,
      placeOnCutUpper,
      placeOnCutLower,
      flipUpper,
      flipLower,
      cutToParts,
    });
  }, [
    meshRef,
    actualPlanePos,
    cutAxis,
    keepUpper,
    keepLower,
    placeOnCutUpper,
    placeOnCutLower,
    flipUpper,
    flipLower,
    cutToParts,
    onCutComplete,
    onCutCancel,
  ]);

  const handleReset = useCallback(() => {
    setCutHeights({ x: 0.5, y: 0.5, z: 0.5 });
    setCutAxis('z');
    setKeepUpper(true);
    setKeepLower(true);
    setPlaceOnCutUpper(true);
    setPlaceOnCutLower(false);
    setFlipUpper(false);
    setFlipLower(false);
    setCutToParts(false);
  }, []);

  // Sync plane position with model. The plane is world-axis aligned (the cut
  // happens in world space), so its rotation does not depend on the model's
  // orientation. Position is in world coords; place the plane at the world-axis
  // center of the model along the two non-cut axes so it visibly spans the model.
  useFrame(() => {
    const obj = meshRef.current;
    if (!obj || !planeRef.current || !handleRef.current) return;
    const geo: THREE.BufferGeometry | undefined = obj?.userData.geometry;
    if (!geo) return;
    obj.updateMatrixWorld();
    geo.computeBoundingBox();
    const bb = geo.boundingBox;
    if (!bb) return;
    // World-space center of the model along non-cut axes.
    const center = new THREE.Vector3();
    bb.getCenter(center).applyMatrix4(obj.matrixWorld);

    if (cutAxis === 'z') {
      planeRef.current.position.set(center.x, center.y, actualPlanePos);
      planeRef.current.rotation.set(0, 0, 0);
    } else if (cutAxis === 'x') {
      planeRef.current.position.set(actualPlanePos, center.y, center.z);
      planeRef.current.rotation.set(0, Math.PI / 2, 0);
    } else {
      planeRef.current.position.set(center.x, actualPlanePos, center.z);
      planeRef.current.rotation.set(Math.PI / 2, 0, 0);
    }

    planeRef.current.scale.set(1, 1, 1);
    handleRef.current.position.copy(planeRef.current.position);
  });

  if (!active) return null;

  return (
    <>
      <group>
        {/* Cutting plane visualization */}
        <mesh ref={planeRef} renderOrder={2}>
          <planeGeometry args={[planeSize, planeSize]} />
          <meshBasicMaterial
            color="#ff6b35"
            transparent
            opacity={0.25}
            side={THREE.DoubleSide}
            depthWrite={false}
            toneMapped={false}
          />
        </mesh>

        {/* Drag handle sphere */}
        <mesh ref={handleRef} renderOrder={4}>
          <sphereGeometry args={[3, 16, 16]} />
          <meshBasicMaterial color="#ff1744" depthTest={false} toneMapped={false} />
        </mesh>
      </group>

      {/* OrcaSlicer-style control panel */}
      {meshRef.current && (
        <Html
          position={[
            meshRef.current.position.x + planeSize / 2 + 30,
            meshRef.current.position.y,
            meshRef.current.position.z + 50,
          ]}
          style={{ pointerEvents: 'auto' }}
        >
          <div className="bg-pf-bg-2/95 backdrop-blur-sm rounded-lg border border-pf-border shadow-xl p-4 text-sm text-pf-text-primary w-80 select-none">
            {/* Mode selector */}
            <div className="flex items-center justify-between mb-3">
              <span className="text-pf-text-secondary">Mode</span>
              <div className="bg-pf-bg-3 border border-pf-border rounded px-2 py-1 text-xs opacity-50 cursor-not-allowed">
                Planar
              </div>
            </div>

            {/* Build volume */}
            <div className="mb-3 text-xs text-pf-text-secondary">
              Build Volume: {bedConfig.width}×{bedConfig.depth}×{bedConfig.height} mm
            </div>

            {/* Cut position */}
            <div className="mb-3">
              <div className="text-pf-text-secondary mb-1.5">Cut position</div>
              <div className="flex items-center gap-2">
                <Button
                  variant="ghost"
                  size="sm"
                  className="bg-pf-bg-3 border border-pf-border rounded px-2 py-1 text-xs min-w-[3rem]"
                  onClick={() => {
                    const nextAxis = cutAxis === 'x' ? 'y' : cutAxis === 'y' ? 'z' : 'x';
                    setCutAxis(nextAxis);
                  }}
                >
                  {cutAxis.toUpperCase()}
                </Button>
                <Input
                  type="number"
                  step="0.01"
                  value={actualPlanePos.toFixed(2)}
                  onChange={(e) => {
                    const val = parseFloat(e.target.value);
                    if (!isNaN(val)) {
                      const normalized = (val - modelBounds.min) / (modelBounds.max - modelBounds.min);
                      setCutHeight(Math.max(0.02, Math.min(0.98, normalized)));
                    }
                  }}
                  className="flex-1 text-xs"
                />
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => setCutHeight(0.5)}
                  className="px-2"
                  title="Reset position to center"
                >
                  ↺
                </Button>
              </div>
            </div>

            {/* Action buttons */}
            <div className="flex gap-2 mb-4 pb-4 border-b border-pf-border">
              <Button
                variant="secondary"
                size="sm"
                disabled
                className="flex-1 text-xs opacity-50 cursor-not-allowed"
                title="Coming soon"
              >
                Add connectors
              </Button>
              <Button
                variant="secondary"
                size="sm"
                onClick={handleReset}
                className="flex-1 text-xs"
              >
                Reset cut
              </Button>
            </div>

            {/* After cut section */}
            <div className="mb-4">
              <div className="text-pf-text-secondary mb-2">After cut:</div>
              
              {/* Upper part — labelled per axis ("Upper" only makes sense for Z) */}
              <div className="flex items-center gap-2 mb-2">
                <div className="w-4 h-4 rounded" style={{ backgroundColor: '#009688' }}></div>
                <span className="text-xs">{cutAxis === 'z' ? 'Upper part' : `+${cutAxis.toUpperCase()} part`}</span>
                <Checkbox
                  checked={keepUpper}
                  onCheckedChange={(c) => setKeepUpper(c as boolean)}
                  id="keep-upper"
                  className="ml-auto"
                />
                <label htmlFor="keep-upper" className="text-xs cursor-pointer">Keep</label>
                <Checkbox
                  checked={placeOnCutUpper}
                  onCheckedChange={(c) => setPlaceOnCutUpper(c as boolean)}
                  id="place-upper"
                />
                <label htmlFor="place-upper" className="text-xs cursor-pointer">Place on cut</label>
                <Checkbox
                  checked={flipUpper}
                  onCheckedChange={(c) => setFlipUpper(c as boolean)}
                  id="flip-upper"
                />
                <label htmlFor="flip-upper" className="text-xs cursor-pointer">Flip</label>
              </div>

              {/* Lower part — labelled per axis */}
              <div className="flex items-center gap-2 mb-2">
                <div className="w-4 h-4 rounded" style={{ backgroundColor: '#9c27b0' }}></div>
                <span className="text-xs">{cutAxis === 'z' ? 'Lower part' : `−${cutAxis.toUpperCase()} part`}</span>
                <Checkbox
                  checked={keepLower}
                  onCheckedChange={(c) => setKeepLower(c as boolean)}
                  id="keep-lower"
                  className="ml-auto"
                />
                <label htmlFor="keep-lower" className="text-xs cursor-pointer">Keep</label>
                <Checkbox
                  checked={placeOnCutLower}
                  onCheckedChange={(c) => setPlaceOnCutLower(c as boolean)}
                  id="place-lower"
                />
                <label htmlFor="place-lower" className="text-xs cursor-pointer">Place on cut</label>
                <Checkbox
                  checked={flipLower}
                  onCheckedChange={(c) => setFlipLower(c as boolean)}
                  id="flip-lower"
                />
                <label htmlFor="flip-lower" className="text-xs cursor-pointer">Flip</label>
              </div>

              {/* Cut to parts */}
              <div className="flex items-center gap-2">
                <Checkbox
                  checked={cutToParts}
                  onCheckedChange={(c) => setCutToParts(c as boolean)}
                  id="cut-parts"
                />
                <label htmlFor="cut-parts" className="text-xs cursor-pointer">Cut to parts</label>
              </div>
            </div>

            {/* Perform cut button */}
            <Button
              variant="primary"
              size="sm"
              onClick={handleConfirm}
              className="w-full"
            >
              Perform cut
            </Button>
          </div>
        </Html>
      )}
    </>
  );
}

export default CutPlaneOverlay;
