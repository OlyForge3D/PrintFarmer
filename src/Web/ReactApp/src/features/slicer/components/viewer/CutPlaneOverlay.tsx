/**
 * Cut Plane Overlay for plane-based model splitting.
 * Shows a visual cutting plane on the selected model that can be
 * dragged up/down to set the cut height, with confirm/cancel UI.
 */
import { useRef, useState, useEffect, useCallback, useMemo } from 'react';
import { useThree, useFrame } from '@react-three/fiber';
import { Html } from '@react-three/drei';
import * as THREE from 'three';
import { Button } from '@/common/components/ui';

interface CutPlaneOverlayProps {
  /** Reference to the selected model's Object3D */
  meshRef: React.RefObject<THREE.Object3D | null>;
  /** Whether cut mode is active */
  active: boolean;
  /** Called when the cut is confirmed with two new geometries */
  onCutComplete: (geometryAbove: THREE.BufferGeometry, geometryBelow: THREE.BufferGeometry) => void;
  /** Called when cut is cancelled */
  onCutCancel: () => void;
}

/** Classify a vertex as above, below, or on the plane */
function classifyPoint(z: number, planeZ: number, epsilon = 1e-6): -1 | 0 | 1 {
  if (z > planeZ + epsilon) return 1;   // above
  if (z < planeZ - epsilon) return -1;  // below
  return 0;                              // on plane
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
 * Split a geometry along a horizontal plane at planeZ.
 * Returns two BufferGeometry objects: above and below.
 * Caps the cut surfaces with new triangles.
 */
function splitGeometryAtPlane(
  geometry: THREE.BufferGeometry,
  planeZ: number,
): { above: THREE.BufferGeometry; below: THREE.BufferGeometry } {
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

    const c0 = classifyPoint(v0.z, planeZ);
    const c1 = classifyPoint(v1.z, planeZ);
    const c2 = classifyPoint(v2.z, planeZ);

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

    // Triangle intersects the plane — split it
    const verts = [v0, v1, v2];
    const classes = [c0, c1, c2];

    // Find the lone vertex (the one on the opposite side from the other two)
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
      // Edge case: two vertices on plane, one off — assign whole triangle
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

    // Compute intersection points
    const tAB = (planeZ - vA.z) / (vB.z - vA.z);
    const tAC = (planeZ - vA.z) / (vC.z - vA.z);
    const pAB = lerpVertex(vA, vB, Math.max(0, Math.min(1, tAB)));
    const pAC = lerpVertex(vA, vC, Math.max(0, Math.min(1, tAC)));

    // Track cap edge
    capEdges.push([pAB.clone(), pAC.clone()]);

    if (cA > 0) {
      // vA is above → 1 triangle above, 2 below
      pushTriVerts(aboveVerts, vA, pAB, pAC);
      pushTriVerts(belowVerts, pAB, vB, vC);
      pushTriVerts(belowVerts, pAB, vC, pAC);
    } else {
      // vA is below → 1 triangle below, 2 above
      pushTriVerts(belowVerts, vA, pAB, pAC);
      pushTriVerts(aboveVerts, pAB, vB, vC);
      pushTriVerts(aboveVerts, pAB, vC, pAC);
    }
  }

  // Build cap faces from the intersection edges using fan triangulation
  if (capEdges.length > 0) {
    // Compute centroid of all cap edge points
    const centroid = new THREE.Vector3();
    const capPoints: THREE.Vector3[] = [];
    for (const [a, b] of capEdges) {
      capPoints.push(a, b);
      centroid.add(a).add(b);
    }
    centroid.divideScalar(capPoints.length);
    centroid.z = planeZ;

    // Fan triangulate from centroid for each edge
    for (const [a, b] of capEdges) {
      // Cap for above (normal pointing down, -Z)
      pushTriVerts(aboveVerts, centroid, b, a);
      // Cap for below (normal pointing up, +Z)
      pushTriVerts(belowVerts, centroid, a, b);
    }
  }

  const makeGeo = (verts: number[]): THREE.BufferGeometry => {
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(verts, 3));
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
  onCutComplete,
  onCutCancel,
}: CutPlaneOverlayProps) {
  const { gl, camera, invalidate } = useThree();
  const planeRef = useRef<THREE.Mesh>(null);
  const [cutHeight, setCutHeight] = useState(0.5); // Normalized 0-1 within model bounds
  const isDraggingRef = useRef(false);
  const raycaster = useMemo(() => new THREE.Raycaster(), []);

  // Compute model bounds in local space
  const modelBounds = useMemo(() => {
    const geo: THREE.BufferGeometry | undefined = meshRef.current?.userData.geometry;
    if (!geo) return { min: 0, max: 10, center: new THREE.Vector3() };
    geo.computeBoundingBox();
    const bb = geo.boundingBox!;
    return {
      min: bb.min.z,
      max: bb.max.z,
      center: new THREE.Vector3(
        (bb.min.x + bb.max.x) / 2,
        (bb.min.y + bb.max.y) / 2,
        (bb.min.z + bb.max.z) / 2,
      ),
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [meshRef.current, active]);

  const actualPlaneZ = modelBounds.min + cutHeight * (modelBounds.max - modelBounds.min);

  // Plane size based on model extents
  const planeSize = useMemo(() => {
    const geo: THREE.BufferGeometry | undefined = meshRef.current?.userData.geometry;
    if (!geo || !geo.boundingBox) return 100;
    const size = new THREE.Vector3();
    geo.boundingBox.getSize(size);
    return Math.max(size.x, size.y) * 1.4;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [meshRef.current, active]);

  // Drag to move the plane up/down
  useEffect(() => {
    if (!active) return;
    const el = gl.domElement;

    const onPointerDown = (e: PointerEvent) => {
      if (e.button !== 0) return;
      // Check if clicking near the cutting plane
      const rect = el.getBoundingClientRect();
      const ndc = new THREE.Vector2(
        ((e.clientX - rect.left) / rect.width) * 2 - 1,
        -((e.clientY - rect.top) / rect.height) * 2 + 1,
      );
      raycaster.setFromCamera(ndc, camera);
      if (planeRef.current) {
        const hits = raycaster.intersectObject(planeRef.current, false);
        if (hits.length > 0) {
          isDraggingRef.current = true;
          el.style.cursor = 'ns-resize';
          e.preventDefault();
          e.stopPropagation();
        }
      }
    };

    const onPointerMove = (e: PointerEvent) => {
      if (!isDraggingRef.current) return;
      // Map mouse Y movement to cut height change
      const rect = el.getBoundingClientRect();
      const ndcY = -((e.clientY - rect.top) / rect.height) * 2 + 1;
      // Approximate: map NDC to normalized height
      const normalized = Math.max(0.02, Math.min(0.98, (ndcY + 1) / 2));
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
  }, [active, camera, gl.domElement, invalidate, raycaster]);

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
    const geo: THREE.BufferGeometry | undefined = meshRef.current?.userData.geometry;
    if (!geo) return;
    const { above, below } = splitGeometryAtPlane(geo, actualPlaneZ);

    // Only proceed if both halves have triangles
    const aboveCount = above.getAttribute('position')?.count ?? 0;
    const belowCount = below.getAttribute('position')?.count ?? 0;
    if (aboveCount < 3 || belowCount < 3) {
      onCutCancel();
      return;
    }

    onCutComplete(above, below);
  }, [meshRef, actualPlaneZ, onCutComplete, onCutCancel]);

  // Sync plane position with model
  useFrame(() => {
    const obj = meshRef.current;
    if (!obj || !planeRef.current) return;
    planeRef.current.position.set(
      obj.position.x,
      obj.position.y,
      obj.position.z + actualPlaneZ,
    );
    planeRef.current.scale.copy(obj.scale);
  });

  if (!active) return null;

  return (
    <group>
      {/* Cutting plane visualization */}
      <mesh
        ref={planeRef}
        rotation={[0, 0, 0]}
        renderOrder={2}
      >
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

      {/* Cut plane edge ring */}
      <mesh
        position={planeRef.current?.position ?? [0, 0, 0]}
        renderOrder={3}
      >
        <ringGeometry args={[planeSize / 2 - 0.5, planeSize / 2, 64]} />
        <meshBasicMaterial
          color="#ff6b35"
          transparent
          opacity={0.8}
          side={THREE.DoubleSide}
          depthWrite={false}
          toneMapped={false}
        />
      </mesh>

      {/* Height indicator + confirm/cancel UI */}
      {meshRef.current && (
        <Html
          position={[
            meshRef.current.position.x + planeSize / 2 + 5,
            meshRef.current.position.y,
            meshRef.current.position.z + actualPlaneZ,
          ]}
          center
          style={{ pointerEvents: 'auto' }}
        >
          <div className="flex flex-col items-start gap-2 select-none">
            <div className="bg-pf-bg-2/95 backdrop-blur-sm px-2.5 py-1 rounded-md border border-orange-500/60 shadow-lg text-sm font-mono text-pf-text-primary whitespace-nowrap">
              Z: {actualPlaneZ.toFixed(1)} mm
            </div>
            <div className="flex gap-1.5">
              <Button
                variant="success"
                size="sm"
                onClick={handleConfirm}
              >
                ✓ Cut
              </Button>
              <Button
                variant="secondary"
                size="sm"
                onClick={onCutCancel}
              >
                ✕ Cancel
              </Button>
            </div>
          </div>
        </Html>
      )}
    </group>
  );
}

export default CutPlaneOverlay;
