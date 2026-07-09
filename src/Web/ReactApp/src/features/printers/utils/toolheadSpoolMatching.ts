import { colorDistance, INVALID_COLOR_DISTANCE } from '@/common/utils/colorDistance';

const EXACT_MATCH_DELTA_E = 2;
const CLOSE_MATCH_DELTA_E = 10;
const NULL_ASSIGNMENT_PENALTY = INVALID_COLOR_DISTANCE;
const FLOAT_TOLERANCE = 0.000001;

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

interface CandidateWithDistance {
  candidateIndex: number;
  distance: number;
}

interface SearchResult {
  score: number;
  assignments: Array<number | null>;
}

function normalizeMaterial(material: string | null | undefined): string | null {
  const normalized = material?.trim().toLowerCase();
  return normalized ? normalized : null;
}

function compareAssignments(left: Array<number | null>, right: Array<number | null>, spools: LoadedSpoolMatchCandidate[]): number {
  const maxComparableId = Number.MAX_SAFE_INTEGER;

  for (let index = 0; index < left.length; index += 1) {
    const leftAssignment = left[index];
    const rightAssignment = right[index];
    const leftId = leftAssignment == null ? maxComparableId : spools[leftAssignment].spoolId;
    const rightId = rightAssignment == null ? maxComparableId : spools[rightAssignment].spoolId;
    if (leftId !== rightId) return leftId - rightId;
  }

  return 0;
}

function isBetterResult(next: SearchResult, current: SearchResult | null, spools: LoadedSpoolMatchCandidate[]): boolean {
  if (!current) return true;
  if (next.score < current.score - FLOAT_TOLERANCE) return true;
  if (Math.abs(next.score - current.score) <= FLOAT_TOLERANCE) {
    return compareAssignments(next.assignments, current.assignments, spools) < 0;
  }
  return false;
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
  const spools = [...uniqueSpools.values()].sort((left, right) => left.spoolId - right.spoolId);
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
    .filter(({ target }) => colorDistance(target.colorHex, '#000000') < INVALID_COLOR_DISTANCE);

  if (matchableTargets.length === 0 || spools.length === 0) return matches;

  const distances = matchableTargets.map(({ target }) => spools
    .map((spool, candidateIndex): CandidateWithDistance => ({
      candidateIndex,
      distance: colorDistance(target.colorHex, spool.colorHex),
    }))
    .filter(candidate => candidate.distance < INVALID_COLOR_DISTANCE)
    .sort((left, right) => {
      if (Math.abs(left.distance - right.distance) > FLOAT_TOLERANCE) {
        return left.distance - right.distance;
      }
      return spools[left.candidateIndex].spoolId - spools[right.candidateIndex].spoolId;
    }));

  const search = (
    matchableIndex: number,
    usedCandidateIndexes: Set<number>,
    currentAssignments: Array<number | null>,
    currentScore: number,
  ): SearchResult => {
    if (matchableIndex >= matchableTargets.length) {
      return { score: currentScore, assignments: [...currentAssignments] };
    }

    const remainingTargets = matchableTargets.length - matchableIndex;
    const remainingCandidates = spools.length - usedCandidateIndexes.size;
    const allowNull = remainingCandidates < remainingTargets;
    let best: SearchResult | null = null;

    for (const candidate of distances[matchableIndex]) {
      if (usedCandidateIndexes.has(candidate.candidateIndex)) continue;

      usedCandidateIndexes.add(candidate.candidateIndex);
      currentAssignments[matchableIndex] = candidate.candidateIndex;
      const result = search(
        matchableIndex + 1,
        usedCandidateIndexes,
        currentAssignments,
        currentScore + candidate.distance,
      );
      if (isBetterResult(result, best, spools)) best = result;
      usedCandidateIndexes.delete(candidate.candidateIndex);
      currentAssignments[matchableIndex] = null;
    }

    if (allowNull || distances[matchableIndex].length === 0) {
      currentAssignments[matchableIndex] = null;
      const result = search(
        matchableIndex + 1,
        usedCandidateIndexes,
        currentAssignments,
        currentScore + NULL_ASSIGNMENT_PENALTY,
      );
      if (isBetterResult(result, best, spools)) best = result;
      currentAssignments[matchableIndex] = null;
    }

    return best ?? { score: currentScore + NULL_ASSIGNMENT_PENALTY, assignments: [...currentAssignments] };
  };

  const best = search(0, new Set<number>(), Array<number | null>(matchableTargets.length).fill(null), 0);

  best.assignments.forEach((candidateIndex, matchableIndex) => {
    const target = matchableTargets[matchableIndex];
    if (candidateIndex == null) return;

    const spool = spools[candidateIndex];
    const deltaE = colorDistance(target.target.colorHex, spool.colorHex);
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
