/**
 * Auto-orient math — pure, framework-free helpers for computing the
 * print orientation that minimises model height while avoiding unsupported
 * overhangs. Extracted from the 3D scene so the geometry math can be unit
 * tested and reused for both single-model and whole-plate orientation.
 *
 * All functions operate on a THREE.BufferGeometry expressed in the model's
 * local (centered) space, matching what the scene stores in
 * `userData.geometry` for each loaded model.
 */
import * as THREE from 'three';

export interface MajorFace {
  normal: THREE.Vector3;
  center: THREE.Vector3;
  area: number;
}

/**
 * Detect major planar face groups from a geometry by clustering triangles
 * with similar normals. Returns the most significant faces sorted by area.
 */
export function detectMajorFaces(
  geometry: THREE.BufferGeometry,
  minAreaFraction = 0.005,
  maxFaces = 14,
): MajorFace[] {
  const posAttr = geometry.getAttribute('position');
  if (!posAttr) return [];

  const index = geometry.getIndex();
  const triCount = index ? index.count / 3 : posAttr.count / 3;

  const vA = new THREE.Vector3();
  const vB = new THREE.Vector3();
  const vC = new THREE.Vector3();
  const edge1 = new THREE.Vector3();
  const edge2 = new THREE.Vector3();
  const fn = new THREE.Vector3();

  interface FaceCluster {
    weightedNormal: THREE.Vector3;
    weightedCenter: THREE.Vector3;
    totalArea: number;
  }

  const clusters: FaceCluster[] = [];
  const ANGLE_THRESHOLD = 0.95; // ~18°
  let totalArea = 0;

  for (let i = 0; i < triCount; i++) {
    if (index) {
      vA.fromBufferAttribute(posAttr, index.getX(i * 3));
      vB.fromBufferAttribute(posAttr, index.getX(i * 3 + 1));
      vC.fromBufferAttribute(posAttr, index.getX(i * 3 + 2));
    } else {
      vA.fromBufferAttribute(posAttr, i * 3);
      vB.fromBufferAttribute(posAttr, i * 3 + 1);
      vC.fromBufferAttribute(posAttr, i * 3 + 2);
    }

    edge1.subVectors(vB, vA);
    edge2.subVectors(vC, vA);
    fn.crossVectors(edge1, edge2);
    const area = fn.length() / 2;
    if (area < 1e-6) continue;
    fn.normalize();
    totalArea += area;

    const cx = (vA.x + vB.x + vC.x) / 3;
    const cy = (vA.y + vB.y + vC.y) / 3;
    const cz = (vA.z + vB.z + vC.z) / 3;

    let matched = false;
    for (const cluster of clusters) {
      if (cluster.weightedNormal.clone().normalize().dot(fn) > ANGLE_THRESHOLD) {
        cluster.weightedNormal.addScaledVector(fn, area);
        cluster.weightedCenter.x += cx * area;
        cluster.weightedCenter.y += cy * area;
        cluster.weightedCenter.z += cz * area;
        cluster.totalArea += area;
        matched = true;
        break;
      }
    }

    if (!matched) {
      clusters.push({
        weightedNormal: fn.clone().multiplyScalar(area),
        weightedCenter: new THREE.Vector3(cx * area, cy * area, cz * area),
        totalArea: area,
      });
    }
  }

  const minArea = totalArea * minAreaFraction;
  return clusters
    .filter((c) => c.totalArea >= minArea)
    .map((c) => ({
      normal: c.weightedNormal.normalize(),
      center: c.weightedCenter.divideScalar(c.totalArea),
      area: c.totalArea,
    }))
    .sort((a, b) => b.area - a.area)
    .slice(0, maxFaces);
}

/**
 * Compute the data-model position Z that places the transformed model on the
 * bed (z = 0). The scene offsets each group by halfZ (= -boundingBox.min.z)
 * internally, so the data Z that lands the lowest vertex on the bed is
 * `-halfZ - minScaledRotatedZ`.
 */
export function computeBedPlacementZ(
  geometry: THREE.BufferGeometry,
  q: THREE.Quaternion,
  scale: THREE.Vector3 = new THREE.Vector3(1, 1, 1),
): number {
  const posAttr = geometry.getAttribute('position');
  if (!posAttr || posAttr.count === 0) return 0;

  const v = new THREE.Vector3();
  let minRotatedZ = Infinity;
  for (let i = 0; i < posAttr.count; i++) {
    v.fromBufferAttribute(posAttr, i);
    v.multiply(scale).applyQuaternion(q);
    if (v.z < minRotatedZ) minRotatedZ = v.z;
  }

  geometry.computeBoundingBox();
  const halfZ = geometry.boundingBox ? -geometry.boundingBox.min.z : 0;
  return -halfZ - minRotatedZ;
}

export interface AutoOrientResult {
  /** Best orientation as a Euler rotation [x, y, z] in radians. */
  rotation: [number, number, number];
  /** Best orientation as a quaternion (rotation applied to the model). */
  quaternion: THREE.Quaternion;
}

/**
 * Compute the orientation that minimises model height while penalising
 * unsupported overhangs. Candidate orientations come from the six principal
 * axes plus each detected major face normal; for each, the model is virtually
 * rotated so that candidate normal points down (-Z), and the candidate is
 * scored by `height * (1 + weight * unsupportedOverhangRatio)`.
 *
 * @param geometry centered model geometry (local space)
 * @param scale per-axis scale applied to the model
 */
export function computeAutoOrientation(
  geometry: THREE.BufferGeometry,
  scale: [number, number, number] = [1, 1, 1],
): AutoOrientResult | null {
  const posAttr = geometry.getAttribute('position');
  if (!posAttr || posAttr.count === 0) return null;

  const faces = detectMajorFaces(geometry, 0.005, 20);

  // Candidate normals: 6 principal axes + major-face normals.
  const candidateNormals: THREE.Vector3[] = [
    new THREE.Vector3(1, 0, 0),
    new THREE.Vector3(-1, 0, 0),
    new THREE.Vector3(0, 1, 0),
    new THREE.Vector3(0, -1, 0),
    new THREE.Vector3(0, 0, 1),
    new THREE.Vector3(0, 0, -1),
  ];
  for (const face of faces) {
    candidateNormals.push(face.normal);
  }

  // Precompute per-triangle normals, areas and centroids for overhang scoring.
  const index = geometry.getIndex();
  const triCount = index ? index.count / 3 : posAttr.count / 3;
  const triNormals: THREE.Vector3[] = [];
  const triAreas: number[] = [];
  const triCentroids: THREE.Vector3[] = [];
  let totalArea = 0;
  const tA = new THREE.Vector3(), tB = new THREE.Vector3(), tC = new THREE.Vector3();
  const e1 = new THREE.Vector3(), e2 = new THREE.Vector3(), tn = new THREE.Vector3();
  for (let i = 0; i < triCount; i++) {
    if (index) {
      tA.fromBufferAttribute(posAttr, index.getX(i * 3));
      tB.fromBufferAttribute(posAttr, index.getX(i * 3 + 1));
      tC.fromBufferAttribute(posAttr, index.getX(i * 3 + 2));
    } else {
      tA.fromBufferAttribute(posAttr, i * 3);
      tB.fromBufferAttribute(posAttr, i * 3 + 1);
      tC.fromBufferAttribute(posAttr, i * 3 + 2);
    }
    e1.subVectors(tB, tA);
    e2.subVectors(tC, tA);
    tn.crossVectors(e1, e2);
    const area = tn.length() / 2;
    if (area < 1e-6) {
      triNormals.push(new THREE.Vector3(0, 0, 1));
      triAreas.push(0);
      triCentroids.push(new THREE.Vector3());
      continue;
    }
    triNormals.push(tn.clone().normalize());
    triAreas.push(area);
    triCentroids.push(new THREE.Vector3().addVectors(tA, tB).add(tC).multiplyScalar(1 / 3));
    totalArea += area;
  }

  const OVERHANG_THRESH = -0.5; // ~60° from horizontal
  const OVERHANG_WEIGHT = 2.0;
  const SUPPORT_Z_TOL_MM = 0.8;
  const v = new THREE.Vector3();
  const rn = new THREE.Vector3();
  const rc = new THREE.Vector3();
  const scaleVec = new THREE.Vector3(scale[0], scale[1], scale[2]);
  // A negative scale determinant (mirrored model) flips physical surface normals,
  // so the precomputed triNormals must be sign-corrected before the overhang test.
  const normalSign = Math.sign(scale[0] * scale[1] * scale[2]) || 1;
  let bestQ: THREE.Quaternion | null = null;
  let bestScore = Infinity;

  for (const normal of candidateNormals) {
    const candidateQ = new THREE.Quaternion().setFromUnitVectors(
      normal,
      new THREE.Vector3(0, 0, -1),
    );

    let minZ = Infinity;
    let maxZ = -Infinity;
    for (let i = 0; i < posAttr.count; i++) {
      v.fromBufferAttribute(posAttr, i);
      v.multiply(scaleVec).applyQuaternion(candidateQ);
      if (v.z < minZ) minZ = v.z;
      if (v.z > maxZ) maxZ = v.z;
    }
    const height = maxZ - minZ;

    let overhangArea = 0;
    for (let i = 0; i < triCount; i++) {
      rn.copy(triNormals[i]).multiplyScalar(normalSign).applyQuaternion(candidateQ);
      if (rn.z >= OVERHANG_THRESH) continue;

      rc.copy(triCentroids[i]).multiply(scaleVec).applyQuaternion(candidateQ);
      const isBedSupported = rc.z <= minZ + SUPPORT_Z_TOL_MM;
      if (!isBedSupported) {
        overhangArea += triAreas[i];
      }
    }
    const overhangRatio = totalArea > 0 ? overhangArea / totalArea : 0;

    const score = height * (1 + OVERHANG_WEIGHT * overhangRatio);
    if (score < bestScore) {
      bestScore = score;
      bestQ = candidateQ;
    }
  }

  if (!bestQ) return null;
  const euler = new THREE.Euler().setFromQuaternion(bestQ);
  return {
    rotation: [euler.x, euler.y, euler.z],
    quaternion: bestQ,
  };
}
