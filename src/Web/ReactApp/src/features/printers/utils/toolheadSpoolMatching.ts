import { colorDistance, INVALID_COLOR_DISTANCE } from '@/common/utils/colorDistance';

const EXACT_MATCH_DELTA_E = 2;
const CLOSE_MATCH_DELTA_E = 10;
const NULL_ASSIGNMENT_PENALTY = INVALID_COLOR_DISTANCE;
const INVALID_ASSIGNMENT_PENALTY = INVALID_COLOR_DISTANCE * 2;
const TIE_BREAK_EPSILON = 0.000000001;

export type SpoolMatchConfidence = 'exact' | 'close' | 'poor' | 'unknown';

export interface FilamentMatchTarget {
  toolheadIndex: number;
  colorHex?: string | null;
  material?: string | null;
}

export interface LoadedSpoolMatchCandidate {
  spoolId: number;
  colorHex?: string | null;
  material?: string | null;
}

export interface ToolheadSpoolMatch {
  toolheadIndex: number;
  targetColorHex?: string | null;
  targetMaterial?: string | null;
  spoolId: number | null;
  spoolColorHex?: string | null;
  spoolMaterial?: string | null;
  deltaE: number | null;
  confidence: SpoolMatchConfidence;
  materialMismatch: boolean;
}

function normalizeMaterial(material: string | null | undefined): string | null {
  const normalized = material?.trim().toLowerCase();
  return normalized ? normalized : null;
}

function isValidHexColor(hex: string | null | undefined): boolean {
  return colorDistance(hex, '#000000') < INVALID_COLOR_DISTANCE;
}

function solveHungarian(costs: number[][]): number[] {
  const rowCount = costs.length;
  const columnCount = costs[0]?.length ?? 0;
  if (rowCount === 0 || columnCount === 0) return [];
  if (rowCount > columnCount) {
    throw new Error('Hungarian assignment requires at least as many columns as rows');
  }

  const potentialsByRow = Array(rowCount + 1).fill(0);
  const potentialsByColumn = Array(columnCount + 1).fill(0);
  const matchedRowByColumn = Array(columnCount + 1).fill(0);
  const previousColumn = Array(columnCount + 1).fill(0);

  // Rectangular Hungarian/Munkres shortest augmenting path. Rows are file
  // targets, columns are loaded spools plus high-cost dummy null assignments.
  for (let row = 1; row <= rowCount; row += 1) {
    matchedRowByColumn[0] = row;
    let currentColumn = 0;
    const minColumnCost = Array(columnCount + 1).fill(Number.POSITIVE_INFINITY);
    const usedColumns = Array(columnCount + 1).fill(false);

    do {
      usedColumns[currentColumn] = true;
      const currentRow = matchedRowByColumn[currentColumn];
      let delta = Number.POSITIVE_INFINITY;
      let nextColumn = 0;

      for (let column = 1; column <= columnCount; column += 1) {
        if (usedColumns[column]) continue;

        const reducedCost = costs[currentRow - 1][column - 1]
          - potentialsByRow[currentRow]
          - potentialsByColumn[column];

        if (reducedCost < minColumnCost[column]) {
          minColumnCost[column] = reducedCost;
          previousColumn[column] = currentColumn;
        }

        if (minColumnCost[column] < delta) {
          delta = minColumnCost[column];
          nextColumn = column;
        }
      }

      for (let column = 0; column <= columnCount; column += 1) {
        if (usedColumns[column]) {
          potentialsByRow[matchedRowByColumn[column]] += delta;
          potentialsByColumn[column] -= delta;
        } else {
          minColumnCost[column] -= delta;
        }
      }

      currentColumn = nextColumn;
    } while (matchedRowByColumn[currentColumn] !== 0);

    do {
      const nextColumn = previousColumn[currentColumn];
      matchedRowByColumn[currentColumn] = matchedRowByColumn[nextColumn];
      currentColumn = nextColumn;
    } while (currentColumn !== 0);
  }

  const assignment = Array(rowCount).fill(-1);
  for (let column = 1; column <= columnCount; column += 1) {
    const row = matchedRowByColumn[column];
    if (row > 0) assignment[row - 1] = column - 1;
  }
  return assignment;
}

export function getSpoolMatchConfidence(deltaE: number | null): SpoolMatchConfidence {
  if (deltaE == null || !Number.isFinite(deltaE) || deltaE >= INVALID_COLOR_DISTANCE) return 'unknown';
  if (deltaE <= EXACT_MATCH_DELTA_E) return 'exact';
  if (deltaE <= CLOSE_MATCH_DELTA_E) return 'close';
  return 'poor';
}

export function hasMaterialMismatch(
  targetMaterial: string | null | undefined,
  spoolMaterial: string | null | undefined,
): boolean {
  const target = normalizeMaterial(targetMaterial);
  const spool = normalizeMaterial(spoolMaterial);
  return target != null && spool != null && target !== spool;
}

export function buildFilamentMatchTargets(
  filamentPerExtruderColorHex: string[] | undefined,
  filamentPerExtruderType?: string[],
): FilamentMatchTarget[] {
  if (!filamentPerExtruderColorHex) return [];

  return filamentPerExtruderColorHex.map((colorHex, toolheadIndex) => ({
    toolheadIndex,
    colorHex,
    material: filamentPerExtruderType?.[toolheadIndex],
  }));
}

export function assignSpoolsToToolheads(
  targets: FilamentMatchTarget[],
  loadedSpools: LoadedSpoolMatchCandidate[],
): ToolheadSpoolMatch[] {
  if (targets.length === 0) return [];

  const uniqueSpools = new Map<number, LoadedSpoolMatchCandidate>();
  for (const spool of loadedSpools) {
    if (Number.isFinite(spool.spoolId) && !uniqueSpools.has(spool.spoolId)) {
      uniqueSpools.set(spool.spoolId, spool);
    }
  }
  const spools = [...uniqueSpools.values()]
    .filter(spool => isValidHexColor(spool.colorHex))
    .sort((left, right) => left.spoolId - right.spoolId);
  const matches: ToolheadSpoolMatch[] = targets.map(target => ({
    toolheadIndex: target.toolheadIndex,
    targetColorHex: target.colorHex,
    targetMaterial: target.material,
    spoolId: null,
    deltaE: null,
    confidence: 'unknown',
    materialMismatch: false,
  }));

  const matchableTargets = targets
    .map((target, targetIndex) => ({ target, targetIndex }))
    .filter(({ target }) => isValidHexColor(target.colorHex));

  if (matchableTargets.length === 0) return matches;

  const dummyColumnCount = matchableTargets.length;
  const costs = matchableTargets.map(({ target }, targetOffset) => [
    ...spools.map((spool, spoolOffset) => {
      const distance = colorDistance(target.colorHex, spool.colorHex);
      const assignmentCost = distance >= INVALID_COLOR_DISTANCE ? INVALID_ASSIGNMENT_PENALTY : distance;
      return assignmentCost + ((spoolOffset + 1) * TIE_BREAK_EPSILON);
    }),
    ...Array.from(
      { length: dummyColumnCount },
      (_, dummyOffset) => NULL_ASSIGNMENT_PENALTY + ((targetOffset + dummyOffset + 1) * TIE_BREAK_EPSILON),
    ),
  ]);

  const assignments = solveHungarian(costs);

  assignments.forEach((candidateIndex, matchableIndex) => {
    const target = matchableTargets[matchableIndex];
    if (candidateIndex == null || candidateIndex < 0 || candidateIndex >= spools.length) return;

    const spool = spools[candidateIndex];
    const deltaE = colorDistance(target.target.colorHex, spool.colorHex);
    if (deltaE >= INVALID_COLOR_DISTANCE) return;

    matches[target.targetIndex] = {
      ...matches[target.targetIndex],
      spoolId: spool.spoolId,
      spoolColorHex: spool.colorHex,
      spoolMaterial: spool.material,
      deltaE,
      confidence: getSpoolMatchConfidence(deltaE),
      materialMismatch: hasMaterialMismatch(target.target.material, spool.material),
    };
  });

  return matches;
}
