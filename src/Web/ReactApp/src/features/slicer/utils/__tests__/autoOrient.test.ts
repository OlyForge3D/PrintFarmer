import { describe, it, expect } from 'vitest';
import * as THREE from 'three';
import {
  detectMajorFaces,
  computeBedPlacementZ,
  computeAutoOrientation,
  assessOrientationStability,
} from '../autoOrient';

/** Height (max-min Z) of a geometry after applying a quaternion + scale. */
function rotatedHeight(geometry: THREE.BufferGeometry, q: THREE.Quaternion, scale: THREE.Vector3): number {
  const pos = geometry.getAttribute('position');
  const v = new THREE.Vector3();
  let minZ = Infinity;
  let maxZ = -Infinity;
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i);
    v.multiply(scale).applyQuaternion(q);
    minZ = Math.min(minZ, v.z);
    maxZ = Math.max(maxZ, v.z);
  }
  return maxZ - minZ;
}

describe('autoOrient', () => {
  describe('detectMajorFaces', () => {
    it('finds the six axis-aligned faces of a box', () => {
      const box = new THREE.BoxGeometry(20, 20, 20).toNonIndexed();
      const faces = detectMajorFaces(box);
      expect(faces.length).toBe(6);
      // Faces are sorted by area; all faces of a cube are equal.
      expect(faces[0].area).toBeGreaterThan(0);
    });

    it('returns [] for geometry without a position attribute', () => {
      const empty = new THREE.BufferGeometry();
      expect(detectMajorFaces(empty)).toEqual([]);
    });
  });

  describe('computeAutoOrientation', () => {
    it('returns null for empty geometry', () => {
      const empty = new THREE.BufferGeometry();
      expect(computeAutoOrientation(empty)).toBeNull();
    });

    it('lays a tall box down to minimise height', () => {
      // 10 (x) x 10 (y) x 40 (z) — tall along Z.
      const tall = new THREE.BoxGeometry(10, 10, 40).toNonIndexed();
      const result = computeAutoOrientation(tall);
      expect(result).not.toBeNull();

      const q = result!.quaternion;
      const height = rotatedHeight(tall, q, new THREE.Vector3(1, 1, 1));
      // Best orientation should reduce the 40mm height toward the 10mm footprint.
      expect(height).toBeLessThan(40 - 1e-3);
      expect(height).toBeCloseTo(10, 3);
    });

    it('returns a rotation triple and matching quaternion', () => {
      const box = new THREE.BoxGeometry(10, 10, 30).toNonIndexed();
      const result = computeAutoOrientation(box);
      expect(result).not.toBeNull();
      expect(result!.rotation).toHaveLength(3);
      const fromEuler = new THREE.Quaternion().setFromEuler(
        new THREE.Euler(result!.rotation[0], result!.rotation[1], result!.rotation[2]),
      );
      // Euler reconstruction matches the returned quaternion.
      expect(Math.abs(fromEuler.dot(result!.quaternion))).toBeCloseTo(1, 4);
    });
  });

  describe('computeBedPlacementZ', () => {
    it('returns a finite Z that rests the model on the bed', () => {
      const box = new THREE.BoxGeometry(10, 10, 20);
      const identity = new THREE.Quaternion();
      const z = computeBedPlacementZ(box, identity, new THREE.Vector3(1, 1, 1));
      expect(Number.isFinite(z)).toBe(true);
    });

    it('returns 0 when geometry has no position attribute', () => {
      const empty = new THREE.BufferGeometry();
      expect(computeBedPlacementZ(empty, new THREE.Quaternion())).toBe(0);
    });

    it('returns a finite Z (not -Infinity) for an empty position buffer', () => {
      // A position attribute with zero vertices must not produce -Infinity, which
      // would launch the model off the bed.
      const degenerate = new THREE.BufferGeometry();
      degenerate.setAttribute('position', new THREE.BufferAttribute(new Float32Array(0), 3));
      const z = computeBedPlacementZ(degenerate, new THREE.Quaternion());
      expect(Number.isFinite(z)).toBe(true);
      expect(z).toBe(0);
    });
  });

  describe('assessOrientationStability', () => {
    it('flags a tall triangular-prism part balanced on its knife-edge as likely unslicable', () => {
      // CylinderGeometry with 3 radial segments = a triangular prism. Default
      // axis is Y; rotate it so the prism's long axis (and its thin ridge/
      // knife edge) points along Z, standing the part up on its edge — the
      // "tall thin part on a likely-unslicable footprint" scenario from #1815.
      const prism = new THREE.CylinderGeometry(15, 15, 60, 3).toNonIndexed();
      prism.rotateX(Math.PI / 2);
      prism.center();

      const knifeEdgeUp = new THREE.Quaternion(); // current orientation: standing on the edge
      const assessment = assessOrientationStability(prism, knifeEdgeUp);
      expect(assessment).not.toBeNull();
      expect(assessment!.isLikelyUnslicable).toBe(true);
      expect(assessment!.currentScore).toBeGreaterThan(assessment!.bestScore);
    });

    it('does not flag a model already resting flat on its largest face', () => {
      // A flat, wide box — already resting on its largest face, same as a
      // well-designed print-in-place model. Auto-orient would pick this
      // exact orientation, so the current/best scores should be equal.
      const flatBox = new THREE.BoxGeometry(80, 60, 10).toNonIndexed();
      const identity = new THREE.Quaternion();
      const assessment = assessOrientationStability(flatBox, identity);
      expect(assessment).not.toBeNull();
      expect(assessment!.isLikelyUnslicable).toBe(false);
      expect(assessment!.currentScore).toBeCloseTo(assessment!.bestScore, 5);
    });

    it('does not flag a short part even if not perfectly optimal', () => {
      // Height is below the noise-floor threshold — should never warn
      // regardless of ratio, to avoid flicker on tiny parts.
      const tinyBox = new THREE.BoxGeometry(2, 2, 2).toNonIndexed();
      const identity = new THREE.Quaternion();
      const assessment = assessOrientationStability(tinyBox, identity);
      expect(assessment).not.toBeNull();
      expect(assessment!.isLikelyUnslicable).toBe(false);
    });

    it('returns null for empty geometry', () => {
      const empty = new THREE.BufferGeometry();
      expect(assessOrientationStability(empty, new THREE.Quaternion())).toBeNull();
    });
  });

  describe('mirrored models', () => {
    it('produces a finite, on-bed orientation for a negatively-scaled (mirrored) box', () => {
      const tall = new THREE.BoxGeometry(10, 10, 40).toNonIndexed();
      const result = computeAutoOrientation(tall, [-1, 1, 1]);
      expect(result).not.toBeNull();
      const z = computeBedPlacementZ(tall, result!.quaternion, new THREE.Vector3(-1, 1, 1));
      expect(Number.isFinite(z)).toBe(true);
      // Mirrored tall box should still be laid down (height reduced from 40).
      const height = rotatedHeight(tall, result!.quaternion, new THREE.Vector3(-1, 1, 1));
      expect(height).toBeLessThan(40 - 1e-3);
    });
  });
});
