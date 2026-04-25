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

/** Classify a vertex as above, below, or on the plane along specified axis */
function classifyPoint(value: number, planeValue: number, epsilon = 1e-6): -1 | 0 | 1 {
  if (value > planeValue + epsilon) return 1;
  if (value < planeValue - epsilon) return -1;
  return 0;
}

/** Linearly interpolate between two 3D points */
function lerpVertex(a: THREE.Vector3, b: THREE.Vector3, t: number): THREE.Vector3 {
  return new THREE.Vector3(
    a.x + (b.x - a.x) * t,
    a.y + (b.y - a.y) * t,
    a.z + (b.z - a.z) * t,
  );
}

/**
 * Order cap edges into a closed polygon by matching endpoints.
 * Returns vertex loop (without repeating the first vertex at the end).
 */
function orderCapEdges(edges: Array<[THREE.Vector3, THREE.Vector3]>, epsilon = 1e-5): THREE.Vector3[] {
  if (edges.length === 0) return [];
  const eq = (a: THREE.Vector3, b: THREE.Vector3) => a.distanceTo(b) < epsilon;

  const polygon: THREE.Vector3[] = [edges[0][0], edges[0][1]];
  const used = new Set([0]);

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
  }

  // Remove closing duplicate
  if (polygon.length > 1 && eq(polygon[0], polygon[polygon.length - 1])) {
    polygon.pop();
  }
  return polygon;
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
 * Triangulate a polygon using ear-clipping. Projects to 2D based on cut axis.
 * Returns array of [v0, v1, v2] triangles with the original 3D vertices.
 */
function earClipTriangulate(
  polygon: THREE.Vector3[],
  axis: CutAxis,
): Array<[THREE.Vector3, THREE.Vector3, THREE.Vector3]> {
  if (polygon.length < 3) return [];
  if (polygon.length === 3) return [[polygon[0], polygon[1], polygon[2]]];

  const project = (v: THREE.Vector3): [number, number] => {
    if (axis === 'x') return [v.y, v.z];
    if (axis === 'y') return [v.x, v.z];
    return [v.x, v.y];
  };

  // Compute signed area to determine winding
  let signedArea = 0;
  for (let i = 0; i < polygon.length; i++) {
    const [x1, y1] = project(polygon[i]);
    const [x2, y2] = project(polygon[(i + 1) % polygon.length]);
    signedArea += (x2 - x1) * (y2 + y1);
  }
  const isCCW = signedArea < 0;

  const indices = Array.from({ length: polygon.length }, (_, i) => i);
  const triangles: Array<[THREE.Vector3, THREE.Vector3, THREE.Vector3]> = [];

  let safety = indices.length * 2;
  while (indices.length > 3 && safety-- > 0) {
    let earFound = false;
    for (let i = 0; i < indices.length; i++) {
      const prevIdx = indices[(i + indices.length - 1) % indices.length];
      const currIdx = indices[i];
      const nextIdx = indices[(i + 1) % indices.length];

      const [px, py] = project(polygon[prevIdx]);
      const [cx, cy] = project(polygon[currIdx]);
      const [nx, ny] = project(polygon[nextIdx]);

      const cross = (cx - px) * (ny - py) - (cy - py) * (nx - px);
      const isConvex = isCCW ? cross > 0 : cross < 0;
      if (!isConvex) continue;

      let containsPoint = false;
      for (const idx of indices) {
        if (idx === prevIdx || idx === currIdx || idx === nextIdx) continue;
        const [tx, ty] = project(polygon[idx]);
        if (pointInTriangle2D(tx, ty, px, py, cx, cy, nx, ny)) {
          containsPoint = true;
          break;
        }
      }
      if (containsPoint) continue;

      triangles.push([polygon[prevIdx], polygon[currIdx], polygon[nextIdx]]);
      indices.splice(i, 1);
      earFound = true;
      break;
    }
    if (!earFound) break;
  }

  if (indices.length === 3) {
    triangles.push([polygon[indices[0]], polygon[indices[1]], polygon[indices[2]]]);
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
 * Split geometry along a plane at specified position on the given axis.
 */
function splitGeometryAtPlane(
  geometry: THREE.BufferGeometry,
  axis: CutAxis,
  planePosition: number,
  modelMatrix?: THREE.Matrix4,
): { above: THREE.BufferGeometry; below: THREE.BufferGeometry } {
  // Transform world-space cutting plane into model-local space
  let localPlanePos = planePosition;
  if (modelMatrix) {
    const inv = modelMatrix.clone().invert();
    const planePoint = axis === 'x'
      ? new THREE.Vector3(planePosition, 0, 0)
      : axis === 'y'
      ? new THREE.Vector3(0, planePosition, 0)
      : new THREE.Vector3(0, 0, planePosition);
    planePoint.applyMatrix4(inv);
    localPlanePos = axis === 'x' ? planePoint.x : axis === 'y' ? planePoint.y : planePoint.z;
  }

  const posAttr = geometry.getAttribute('position');
  const index = geometry.getIndex();
  const triCount = index ? index.count / 3 : posAttr.count / 3;

  const aboveVerts: number[] = [];
  const belowVerts: number[] = [];
  const capEdges: Array<[THREE.Vector3, THREE.Vector3]> = [];

  const getVertex = (i: number): THREE.Vector3 => {
    return new THREE.Vector3(
      posAttr.getX(i),
      posAttr.getY(i),
      posAttr.getZ(i),
    );
  };

  const getAxisValue = (v: THREE.Vector3): number => {
    return axis === 'x' ? v.x : axis === 'y' ? v.y : v.z;
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

    const c0 = classifyPoint(getAxisValue(v0), localPlanePos);
    const c1 = classifyPoint(getAxisValue(v1), localPlanePos);
    const c2 = classifyPoint(getAxisValue(v2), localPlanePos);

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

    // Triangle intersects the plane
    const verts = [v0, v1, v2];
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
      if (c0 > 0 || c1 > 0 || c2 > 0) {
        pushTriVerts(aboveVerts, v0, v1, v2);
      } else {
        pushTriVerts(belowVerts, v0, v1, v2);
      }
      continue;
    }

    const vA = verts[loneIdx];
    const vB = verts[(loneIdx + 1) % 3];
    const vC = verts[(loneIdx + 2) % 3];
    const cA = classes[loneIdx];

    const valA = getAxisValue(vA);
    const valB = getAxisValue(vB);
    const valC = getAxisValue(vC);
    const tAB = (localPlanePos - valA) / (valB - valA);
    const tAC = (localPlanePos - valA) / (valC - valA);
    const pAB = lerpVertex(vA, vB, Math.max(0, Math.min(1, tAB)));
    const pAC = lerpVertex(vA, vC, Math.max(0, Math.min(1, tAC)));

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

  // Build cap faces using ordered polygon + ear-clipping triangulation
  if (capEdges.length > 0) {
    const polygon = orderCapEdges(capEdges);
    if (polygon.length >= 3) {
      const tris = earClipTriangulate(polygon, axis);
      for (const [a, b, c] of tris) {
        // Above cap: winding opposite to below
        pushTriVerts(aboveVerts, c, b, a);
        pushTriVerts(belowVerts, a, b, c);
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
  const [cutHeight, setCutHeight] = useState(0.5);
  const [keepUpper, setKeepUpper] = useState(true);
  const [keepLower, setKeepLower] = useState(true);
  const [placeOnCutUpper, setPlaceOnCutUpper] = useState(true);
  const [placeOnCutLower, setPlaceOnCutLower] = useState(false);
  const [flipUpper, setFlipUpper] = useState(false);
  const [flipLower, setFlipLower] = useState(false);
  const [cutToParts, setCutToParts] = useState(false);
  const isDraggingRef = useRef(false);
  const raycaster = useMemo(() => new THREE.Raycaster(), []);

  // Reset height when axis changes
  useEffect(() => {
    setCutHeight(0.5);
  }, [cutAxis]);

  // Compute model bounds for current axis
  const modelBounds = useMemo(() => {
    const geo: THREE.BufferGeometry | undefined = meshRef.current?.userData.geometry;
    if (!geo) return { min: 0, max: 10, center: new THREE.Vector3() };
    geo.computeBoundingBox();
    const bb = geo.boundingBox!;
    const axisMin = cutAxis === 'x' ? bb.min.x : cutAxis === 'y' ? bb.min.y : bb.min.z;
    const axisMax = cutAxis === 'x' ? bb.max.x : cutAxis === 'y' ? bb.max.y : bb.max.z;
    return {
      min: axisMin,
      max: axisMax,
      center: new THREE.Vector3(
        (bb.min.x + bb.max.x) / 2,
        (bb.min.y + bb.max.y) / 2,
        (bb.min.z + bb.max.z) / 2,
      ),
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [meshRef.current, active, cutAxis]);

  const actualPlanePos = modelBounds.min + cutHeight * (modelBounds.max - modelBounds.min);

  // Plane size based on model extents
  const planeSize = useMemo(() => {
    const geo: THREE.BufferGeometry | undefined = meshRef.current?.userData.geometry;
    if (!geo || !geo.boundingBox) return 100;
    const size = new THREE.Vector3();
    geo.boundingBox.getSize(size);
    return Math.max(size.x, size.y, size.z) * 1.4;
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

    const objPos = cutAxis === 'x' ? obj.position.x : cutAxis === 'y' ? obj.position.y : obj.position.z;
    const objScale = cutAxis === 'x' ? obj.scale.x : cutAxis === 'y' ? obj.scale.y : obj.scale.z;
    const worldPlanePos = objPos + (modelBounds.min + cutHeight * (modelBounds.max - modelBounds.min)) * objScale;
    const { above, below } = splitGeometryAtPlane(geo, cutAxis, worldPlanePos, obj.matrixWorld);

    const aboveCount = above.getAttribute('position')?.count ?? 0;
    const belowCount = below.getAttribute('position')?.count ?? 0;
    if (aboveCount < 3 || belowCount < 3) {
      onCutCancel();
      return;
    }

    onCutComplete(above, below, {
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
    modelBounds,
    cutHeight,
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
    setCutHeight(0.5);
    setCutAxis('z');
    setKeepUpper(true);
    setKeepLower(true);
    setPlaceOnCutUpper(true);
    setPlaceOnCutLower(false);
    setFlipUpper(false);
    setFlipLower(false);
    setCutToParts(false);
  }, []);

  // Sync plane position and rotation with model
  useFrame(() => {
    const obj = meshRef.current;
    if (!obj || !planeRef.current || !handleRef.current) return;

    const pos = actualPlanePos;
    
    if (cutAxis === 'z') {
      planeRef.current.position.set(obj.position.x, obj.position.y, obj.position.z + pos * obj.scale.z);
      planeRef.current.rotation.set(0, 0, 0);
    } else if (cutAxis === 'x') {
      planeRef.current.position.set(obj.position.x + pos * obj.scale.x, obj.position.y, obj.position.z);
      planeRef.current.rotation.set(0, Math.PI / 2, 0);
    } else {
      planeRef.current.position.set(obj.position.x, obj.position.y + pos * obj.scale.y, obj.position.z);
      planeRef.current.rotation.set(Math.PI / 2, 0, 0);
    }

    planeRef.current.scale.copy(obj.scale);
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
              
              {/* Upper part */}
              <div className="flex items-center gap-2 mb-2">
                <div className="w-4 h-4 rounded" style={{ backgroundColor: '#009688' }}></div>
                <span className="text-xs">Upper part</span>
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

              {/* Lower part */}
              <div className="flex items-center gap-2 mb-2">
                <div className="w-4 h-4 rounded" style={{ backgroundColor: '#9c27b0' }}></div>
                <span className="text-xs">Lower part</span>
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
