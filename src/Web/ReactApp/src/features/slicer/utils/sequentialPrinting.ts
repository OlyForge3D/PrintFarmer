/**
 * Sequential printing (print-by-object) collision clearance computation.
 * Determines clearance zones, detects collisions, and computes print order.
 */

export interface PrintheadClearance {
  /** Distance from nozzle to left edge of printhead (mm) */
  offsetLeft: number;
  /** Distance from nozzle to right edge of printhead (mm) */
  offsetRight: number;
  /** Distance from nozzle to front edge of printhead (mm) */
  offsetFront: number;
  /** Distance from nozzle to back edge of printhead (mm) */
  offsetBack: number;
  /** Height clearance — maximum height the gantry can pass over a printed part (mm) */
  clearanceHeight: number;
}

export interface ModelFootprint {
  modelId: string;
  /** Model bounding box in bed coordinates (min/max XY) */
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
  /** Model height (Z) — determines if gantry can clear over it */
  height: number;
}

export interface ClearanceZone {
  modelId: string;
  /** The expanded bounding box including printhead clearance */
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
}

export interface CollisionResult {
  modelA: string;
  modelB: string;
}

export interface SequentialPrintOrder {
  /** Ordered list of model IDs in suggested print sequence */
  order: string[];
  /** Any unresolvable collisions */
  collisions: CollisionResult[];
  /** Whether all models can be printed sequentially */
  feasible: boolean;
}

/**
 * Default printhead clearance values matching OrcaSlicer defaults.
 */
export const DEFAULT_PRINTHEAD_CLEARANCE: PrintheadClearance = {
  offsetLeft: 35,
  offsetRight: 35,
  offsetFront: 35,
  offsetBack: 35,
  clearanceHeight: 40,
};

/**
 * Compute clearance zones for each model given printhead dimensions.
 * The clearance zone is the model's XY bounding box expanded by the printhead offsets.
 * Only models taller than clearanceHeight need full expansion; shorter models
 * that the gantry can pass over need no expansion.
 */
export function computeClearanceZones(
  models: ModelFootprint[],
  clearance: PrintheadClearance,
): ClearanceZone[] {
  return models.map((model) => {
    if (model.height <= clearance.clearanceHeight) {
      return {
        modelId: model.modelId,
        minX: model.minX,
        maxX: model.maxX,
        minY: model.minY,
        maxY: model.maxY,
      };
    }

    return {
      modelId: model.modelId,
      minX: model.minX - clearance.offsetLeft,
      maxX: model.maxX + clearance.offsetRight,
      minY: model.minY - clearance.offsetFront,
      maxY: model.maxY + clearance.offsetBack,
    };
  });
}

function zonesOverlap(a: ClearanceZone, b: ClearanceZone): boolean {
  return a.minX < b.maxX && a.maxX > b.minX && a.minY < b.maxY && a.maxY > b.minY;
}

/**
 * Detect collisions between clearance zones.
 * Two models collide if their clearance zones overlap in XY AND
 * at least one model is taller than clearanceHeight.
 */
export function detectCollisions(
  zones: ClearanceZone[],
  models: ModelFootprint[],
  clearanceHeight: number,
): CollisionResult[] {
  const collisions: CollisionResult[] = [];
  const modelMap = new Map(models.map((m) => [m.modelId, m]));

  for (let i = 0; i < zones.length; i++) {
    for (let j = i + 1; j < zones.length; j++) {
      const zoneA = zones[i];
      const zoneB = zones[j];
      const modelA = modelMap.get(zoneA.modelId);
      const modelB = modelMap.get(zoneB.modelId);

      if (!modelA || !modelB) continue;

      const eitherTall = modelA.height > clearanceHeight || modelB.height > clearanceHeight;
      if (eitherTall && zonesOverlap(zoneA, zoneB)) {
        collisions.push({ modelA: zoneA.modelId, modelB: zoneB.modelId });
      }
    }
  }

  return collisions;
}

/**
 * Compute a valid sequential print order using greedy heuristic:
 * Sort models by Y position (back-to-front) to minimize gantry conflicts.
 * Returns the order and any remaining collisions.
 */
export function computePrintOrder(
  models: ModelFootprint[],
  clearance: PrintheadClearance,
): SequentialPrintOrder {
  // Sort back-to-front (ascending Y) to minimize gantry conflicts
  const sorted = [...models].sort((a, b) => {
    const aCenter = (a.minY + a.maxY) / 2;
    const bCenter = (b.minY + b.maxY) / 2;
    return aCenter - bCenter;
  });

  const order = sorted.map((m) => m.modelId);
  const zones = computeClearanceZones(models, clearance);
  const collisions = detectCollisions(zones, models, clearance.clearanceHeight);

  return {
    order,
    collisions,
    feasible: collisions.length === 0,
  };
}
