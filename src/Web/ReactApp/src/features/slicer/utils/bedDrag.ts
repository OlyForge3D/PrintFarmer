/**
 * Pure math for drag-to-move on the XY build plate.
 *
 * Extracted from the R3F drag hook so the offset-sensitive move path can be
 * unit-tested without a live Canvas / TransformControls (neither runs in
 * jsdom).
 */

export interface BedFootprint {
  width: number;
  depth: number;
}

export interface Vec2 {
  x: number;
  y: number;
}

/**
 * Resolves a pointer's world-space XY hit into a clamped, bed-local model
 * position.
 *
 * `grabOffset` is captured at drag start as `model.position(bed-local) -
 * hit(world)`. Because both the start and the live hit are taken in world
 * space, adding the offset yields the bed-local position regardless of which
 * plate the model sits on — the plate's grid offset cancels out. The result is
 * clamped to the bed footprint (also expressed in bed-local coordinates).
 */
export function localizeDragTarget(
  worldHit: Vec2,
  grabOffset: Vec2,
  bed: BedFootprint,
): [number, number] {
  const halfW = bed.width / 2;
  const halfD = bed.depth / 2;
  const nx = Math.max(-halfW, Math.min(halfW, worldHit.x + grabOffset.x));
  const ny = Math.max(-halfD, Math.min(halfD, worldHit.y + grabOffset.y));
  return [nx, ny];
}
