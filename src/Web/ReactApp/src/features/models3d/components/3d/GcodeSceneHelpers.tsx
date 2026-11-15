import React, { useEffect, useMemo, useRef } from 'react';
import { useThree } from '@react-three/fiber';
import * as THREE from 'three';
import type { BedBounds } from '@/features/slicer/services';
import {
  buildBedGridPositions,
  computeCameraFit,
  computeFitDistance,
  type GcodeBounds,
} from './gcodeSceneMath';

const BED_PLATE_COLOR = '#2b2b2b';
// Just below Z=0 so the grid and first layer never z-fight with the plate.
const BED_PLATE_OFFSET_MM = -0.1;

export interface GcodeBedProps {
  bed: BedBounds;
  showGrid: boolean;
  gridColor: string;
}

/**
 * Build plate drawn in G-code coordinates (place inside the Z-up group):
 * a solid plate under the print plus an optional 10 mm grid.
 */
export const GcodeBed: React.FC<GcodeBedProps> = ({ bed, showGrid, gridColor }) => {
  const width = bed.maxX - bed.minX;
  const depth = bed.maxY - bed.minY;

  const grid = useMemo(() => {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(buildBedGridPositions(bed), 3));
    const material = new THREE.LineBasicMaterial({ color: gridColor });
    return new THREE.LineSegments(geometry, material);
  }, [bed, gridColor]);

  useEffect(() => () => {
    grid.geometry.dispose();
    (grid.material as THREE.Material).dispose();
  }, [grid]);

  return (
    <group>
      <mesh position={[bed.minX + width / 2, bed.minY + depth / 2, BED_PLATE_OFFSET_MM]}>
        <planeGeometry args={[width, depth]} />
        <meshBasicMaterial color={BED_PLATE_COLOR} side={THREE.DoubleSide} />
      </mesh>
      {showGrid && <primitive object={grid} />}
    </group>
  );
};

interface FittableControls {
  target: THREE.Vector3;
  update(): void;
}

/**
 * Frames the camera on the toolpath whenever new bounds arrive (i.e. once per
 * loaded file). Layer scrubbing and color changes keep the user's camera; a
 * canvas resize keeps the orbit direction but rescales the distance so the
 * toolpath still fits the new aspect ratio.
 */
export const GcodeCameraFit: React.FC<{ bounds: GcodeBounds | null }> = ({ bounds }) => {
  // Subscribed only so the fit re-runs once OrbitControls registers itself as default.
  const controlsReady = useThree(state => state.controls !== null);
  const get = useThree(state => state.get);
  const width = useThree(state => state.size.width);
  const height = useThree(state => state.size.height);
  const fittedAspect = useRef<number | null>(null);

  useEffect(() => {
    const { camera, size } = get();
    const controls = get().controls as unknown as FittableControls | null;
    if (!bounds || !(camera instanceof THREE.PerspectiveCamera)) return;
    const aspect = size.width / Math.max(1, size.height);
    const fit = computeCameraFit(bounds, camera.fov, aspect);
    fittedAspect.current = aspect;
    camera.position.set(...fit.position);
    camera.near = fit.near;
    camera.far = fit.far;
    camera.updateProjectionMatrix();
    if (controls) {
      controls.target.set(...fit.target);
      controls.update();
    } else {
      camera.lookAt(...fit.target);
    }
  }, [bounds, controlsReady, get]);

  useEffect(() => {
    const previous = fittedAspect.current;
    const { camera } = get();
    const controls = get().controls as unknown as FittableControls | null;
    if (!bounds || previous === null || !controls || !(camera instanceof THREE.PerspectiveCamera)) return;
    const aspect = width / Math.max(1, height);
    if (Math.abs(aspect - previous) < 1e-6) return;
    const scale = computeFitDistance(bounds, camera.fov, aspect)
      / computeFitDistance(bounds, camera.fov, previous);
    fittedAspect.current = aspect;
    const offset = camera.position.clone().sub(controls.target).multiplyScalar(scale);
    camera.position.copy(controls.target).add(offset);
    camera.near *= scale;
    camera.far *= scale;
    camera.updateProjectionMatrix();
    controls.update();
  }, [bounds, width, height, get]);

  return null;
};
