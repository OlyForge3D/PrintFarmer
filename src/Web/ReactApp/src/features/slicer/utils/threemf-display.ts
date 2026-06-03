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

/**
 * Drops .3mf meshes to the bed, grouping by buildItemIndex (unique per build item instance).
 * Meshes with the same buildItemIndex are dropped as a unit (preserving relative Z within the group).
 * Different buildItemIndex groups are dropped independently — matching OrcaSlicer behavior.
 * If no buildItemIndex is provided, all meshes are treated as a single group (global drop).
 */
export function cloneThreeMfMeshesDroppedToBed<T extends { geometry: THREE.BufferGeometry; buildItemIndex?: number }>(
  meshes: readonly T[],
): ThreeMfDroppedMeshes<T> {
  // Clone all geometries up front
  const cloned = meshes.map((mesh) => {
    const geometry = mesh.geometry.clone();
    geometry.computeBoundingBox();
    return { ...mesh, geometry } as ThreeMfDisplayMesh<T>;
  });

  // Group meshes by buildItemIndex; meshes without it go into a single group
  const groups = new Map<number | string, ThreeMfDisplayMesh<T>[]>();
  for (let i = 0; i < cloned.length; i++) {
    const key = meshes[i].buildItemIndex ?? '__all__';
    let group = groups.get(key);
    if (!group) {
      group = [];
      groups.set(key, group);
    }
    group.push(cloned[i]);
  }

  // Drop each group independently so its lowest point sits at Z=0
  for (const group of groups.values()) {
    const groupBounds = new THREE.Box3();
    for (const mesh of group) {
      if (mesh.geometry.boundingBox) {
        groupBounds.union(mesh.geometry.boundingBox);
      }
    }
    const groupMinZ = groupBounds.isEmpty() ? 0 : groupBounds.min.z;
    for (const mesh of group) {
      mesh.geometry.translate(0, 0, -groupMinZ);
      mesh.geometry.computeBoundingBox();
    }
  }

  // Compute combined bounds after all groups are dropped
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
