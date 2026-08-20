/**
 * useNozzleGeometry
 *
 * Memoizes the nozzle cone geometry by diameter so that live position/state
 * updates (which happen far more often than diameter changes) reuse the same
 * Three.js `BufferGeometry` instance instead of allocating a replacement.
 * The previous geometry is disposed whenever the diameter changes and again
 * on unmount, to avoid leaking WebGL buffers.
 */

import { useEffect, useMemo } from 'react';
import * as THREE from 'three';
import { generateNozzleGeometry } from '@/common/utils/bedGeometryGenerator';

export function useNozzleGeometry(nozzleDiameter: number): THREE.BufferGeometry {
  const nozzleGeometry = useMemo(
    () => generateNozzleGeometry(nozzleDiameter),
    [nozzleDiameter]
  );

  useEffect(() => {
    return () => {
      nozzleGeometry.dispose();
    };
  }, [nozzleGeometry]);

  return nozzleGeometry;
}
