import React, { useEffect, useLayoutEffect, useMemo } from 'react';
import * as THREE from 'three';
import type { GcodeLineStream } from './gcodeLineBuffers';

export interface GcodeLineSegmentsProps {
  stream: GcodeLineStream;
  /** Number of vertices to draw (from `stream.layerEnds`). */
  drawCount: number;
  opacity?: number;
}

/**
 * Renders one merged G-code vertex stream as a single `THREE.LineSegments`
 * (one draw call). Layer scrubbing only changes the draw range, so moving
 * the slider never reallocates geometry.
 */
export const GcodeLineSegments: React.FC<GcodeLineSegmentsProps> = ({ stream, drawCount, opacity = 1 }) => {
  const object = useMemo(() => {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(stream.positions, 3));
    geometry.setAttribute('color', new THREE.BufferAttribute(stream.colors, 3, true));
    const material = new THREE.LineBasicMaterial({
      vertexColors: true,
      transparent: opacity < 1,
      opacity,
    });
    const lines = new THREE.LineSegments(geometry, material);
    // Geometry spans the whole print; skip per-frame bounds checks.
    lines.frustumCulled = false;
    return lines;
  }, [stream, opacity]);

  useEffect(() => () => {
    object.geometry.dispose();
    (object.material as THREE.Material).dispose();
  }, [object]);

  useLayoutEffect(() => {
    object.geometry.setDrawRange(0, drawCount);
  }, [object, drawCount]);

  return <primitive object={object} />;
};
