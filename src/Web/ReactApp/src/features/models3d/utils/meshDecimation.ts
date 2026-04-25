import * as THREE from 'three';

export interface DecimationResult {
  geometry: THREE.BufferGeometry;
  originalVertices: number;
  originalFaces: number;
  resultVertices: number;
  resultFaces: number;
  reductionPercent: number;
}

/**
 * Decimates a BufferGeometry using vertex clustering.
 *
 * A 3D grid is overlaid on the bounding box. Every vertex is mapped to a grid
 * cell and replaced by the cell centroid. Faces whose three vertices collapse
 * into fewer than three distinct cells are discarded.
 *
 * @param geometry - Source geometry (not mutated)
 * @param targetReduction - Fraction to remove, 0..1 (e.g. 0.5 = 50 % fewer faces)
 */
export function decimateGeometry(
  geometry: THREE.BufferGeometry,
  targetReduction: number,
): DecimationResult {
  const geo = geometry.clone();

  // Work with non-indexed geometry so every 3 consecutive vertices form a face
  const nonIndexed = geo.index ? geo.toNonIndexed() : geo;

  // Dispose the clone if toNonIndexed created a separate geometry
  if (nonIndexed !== geo) {
    geo.dispose();
  }

  const positions = nonIndexed.getAttribute('position') as THREE.BufferAttribute;
  const originalVertices = positions.count;
  const originalFaces = originalVertices / 3;

  if (targetReduction <= 0 || originalFaces < 100) {
    return {
      geometry: nonIndexed,
      originalVertices,
      originalFaces,
      resultVertices: originalVertices,
      resultFaces: originalFaces,
      reductionPercent: 0,
    };
  }

  nonIndexed.computeBoundingBox();
  const bbox = nonIndexed.boundingBox!;
  const min = bbox.min;
  const size = new THREE.Vector3();
  bbox.getSize(size);

  // Grid resolution derived from the desired face count
  const targetFaces = Math.max(12, Math.floor(originalFaces * (1 - targetReduction)));
  const gridRes = Math.max(4, Math.ceil(Math.pow(targetFaces * 2, 1 / 3) * 1.5));

  const cellSize = new THREE.Vector3(
    size.x / gridRes || 1,
    size.y / gridRes || 1,
    size.z / gridRes || 1,
  );

  // Map each vertex to a grid cell key
  const vertexToCellKey = (x: number, y: number, z: number): string => {
    const cx = Math.min(gridRes - 1, Math.floor((x - min.x) / cellSize.x));
    const cy = Math.min(gridRes - 1, Math.floor((y - min.y) / cellSize.y));
    const cz = Math.min(gridRes - 1, Math.floor((z - min.z) / cellSize.z));
    return `${cx},${cy},${cz}`;
  };

  // Accumulate vertices per cell for centroid calculation
  const cellVertices = new Map<
    string,
    { sumX: number; sumY: number; sumZ: number; count: number; index: number }
  >();
  let nextIndex = 0;

  const vertexCellIndices: number[] = new Array(originalVertices) as number[];

  for (let i = 0; i < originalVertices; i++) {
    const x = positions.getX(i);
    const y = positions.getY(i);
    const z = positions.getZ(i);
    const key = vertexToCellKey(x, y, z);

    let cell = cellVertices.get(key);
    if (!cell) {
      cell = { sumX: 0, sumY: 0, sumZ: 0, count: 0, index: nextIndex++ };
      cellVertices.set(key, cell);
    }
    cell.sumX += x;
    cell.sumY += y;
    cell.sumZ += z;
    cell.count++;
    vertexCellIndices[i] = cell.index;
  }

  // Build centroid position buffer
  const newPositions = new Float32Array(cellVertices.size * 3);
  for (const cell of cellVertices.values()) {
    const idx = cell.index * 3;
    newPositions[idx] = cell.sumX / cell.count;
    newPositions[idx + 1] = cell.sumY / cell.count;
    newPositions[idx + 2] = cell.sumZ / cell.count;
  }

  // Rebuild faces, skipping degenerate triangles
  const newIndices: number[] = [];
  for (let f = 0; f < originalFaces; f++) {
    const i0 = vertexCellIndices[f * 3];
    const i1 = vertexCellIndices[f * 3 + 1];
    const i2 = vertexCellIndices[f * 3 + 2];

    if (i0 !== i1 && i1 !== i2 && i0 !== i2) {
      newIndices.push(i0, i1, i2);
    }
  }

  const newGeo = new THREE.BufferGeometry();
  newGeo.setAttribute('position', new THREE.BufferAttribute(newPositions, 3));
  newGeo.setIndex(newIndices);
  newGeo.computeVertexNormals();
  newGeo.computeBoundingBox();

  // Dispose intermediate geometry now that data has been extracted
  nonIndexed.dispose();

  const resultFaces = newIndices.length / 3;
  const resultVertices = cellVertices.size;

  return {
    geometry: newGeo,
    originalVertices,
    originalFaces,
    resultVertices,
    resultFaces,
    reductionPercent: ((originalFaces - resultFaces) / originalFaces) * 100,
  };
}
