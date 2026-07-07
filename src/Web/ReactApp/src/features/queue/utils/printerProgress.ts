/**
 * Pure reducer for the live print-progress map keyed by printer id.
 *
 * Applies a printer status update to the previous progress map:
 * - A numeric `progress` sets/updates that printer's entry (returning the same
 *   reference when unchanged to avoid needless re-renders).
 * - A non-numeric `progress` (printer idle/finished/unknown) clears any cached
 *   entry so the next job on that printer cannot briefly inherit the previous
 *   job's percentage.
 */
export function mergePrinterProgress(
  prev: Record<string, number>,
  printerId: string,
  progress?: number,
): Record<string, number> {
  if (typeof progress !== "number") {
    if (!(printerId in prev)) return prev;
    const next = { ...prev };
    delete next[printerId];
    return next;
  }
  return prev[printerId] === progress ? prev : { ...prev, [printerId]: progress };
}
