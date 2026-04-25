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
import { splitGeometryAtPlane } from './cutGeometry';

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
  }, [active, camera, cutAxis, gl.domElement, invalidate, raycaster, setCutHeight]);

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
                  disabled
                  title="Coming soon"
                  className="opacity-50 cursor-not-allowed"
                />
                <label htmlFor="flip-upper" className="text-xs opacity-50 cursor-not-allowed" title="Coming soon">Flip</label>
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
                  disabled
                  title="Coming soon"
                  className="opacity-50 cursor-not-allowed"
                />
                <label htmlFor="flip-lower" className="text-xs opacity-50 cursor-not-allowed" title="Coming soon">Flip</label>
              </div>

              {/* Cut to parts */}
              <div className="flex items-center gap-2">
                <Checkbox
                  checked={cutToParts}
                  onCheckedChange={(c) => setCutToParts(c as boolean)}
                  id="cut-parts"
                  disabled
                  title="Coming soon"
                  className="opacity-50 cursor-not-allowed"
                />
                <label htmlFor="cut-parts" className="text-xs opacity-50 cursor-not-allowed" title="Coming soon">Cut to parts</label>
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
