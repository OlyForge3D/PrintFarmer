import * as THREE from 'three';

export type ThreeMfDisplayMesh<T extends { geometry: THREE.BufferGeometry }> = Omit<T, 'geometry'> & {
  geometry: THREE.BufferGeometry;
};

export interface ThreeMfDroppedMeshes<T extends { geometry: THREE.BufferGeometry }> {
  meshes: ThreeMfDisplayMesh<T>[];
  bounds: THREE.Box3;
  center: THREE.Vector3;
  size: THREE.Vector3;
}

export function cloneThreeMfMeshesDroppedToBed<T extends { geometry: THREE.BufferGeometry }>(
  meshes: readonly T[],
): ThreeMfDroppedMeshes<T> {
  // First pass: clone geometries and compute the global bounding box
  const cloned = meshes.map((mesh) => {
    const geometry = mesh.geometry.clone();
    geometry.computeBoundingBox();
    return { ...mesh, geometry } as ThreeMfDisplayMesh<T>;
  });

  const globalBounds = new THREE.Box3();
  for (const mesh of cloned) {
    if (mesh.geometry.boundingBox) {
      globalBounds.union(mesh.geometry.boundingBox);
    }
  }

  // Drop the entire assembly as a unit so the lowest point sits at Z=0
  const globalMinZ = globalBounds.isEmpty() ? 0 : globalBounds.min.z;

  for (const mesh of cloned) {
    mesh.geometry.translate(0, 0, -globalMinZ);
    mesh.geometry.computeBoundingBox();
  }

  // Recompute combined bounds after the uniform drop
  const bounds = new THREE.Box3();
  for (const mesh of cloned) {
    if (mesh.geometry.boundingBox) {
      bounds.union(mesh.geometry.boundingBox);
    }
  }

  const center = bounds.isEmpty()
    ? new THREE.Vector3()
    : bounds.getCenter(new THREE.Vector3());
  const size = bounds.isEmpty()
    ? new THREE.Vector3()
    : bounds.getSize(new THREE.Vector3());

  return {
    meshes: cloned,
    bounds,
    center,
    size,
  };
}
