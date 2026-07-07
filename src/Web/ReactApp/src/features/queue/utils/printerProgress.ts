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

/**
 * Pure reducer for the live printer-side thumbnail map keyed by printer id.
 *
 * Externally-started prints (started directly on the printer, not queued through
 * PrintFarmer) have no local G-code file, so their only artwork is the thumbnail
 * the printer itself reports over SignalR. This reducer caches that URL:
 * - When the printer is actively printing (`active`) and reports a non-empty
 *   `thumbnailUrl`, the entry is set/updated (same reference when unchanged).
 * - When the printer is active but omits a thumbnail in a partial update, the
 *   previous value is preserved to avoid flicker.
 * - When the printer is idle/finished (`active` is false), any cached entry is
 *   cleared so the next job on that printer cannot inherit stale artwork.
 */
export function mergePrinterThumbnail(
  prev: Record<string, string>,
  printerId: string,
  thumbnailUrl: string | undefined,
  active: boolean,
): Record<string, string> {
  if (!active) {
    if (!(printerId in prev)) return prev;
    const next = { ...prev };
    delete next[printerId];
    return next;
  }

  if (!thumbnailUrl) return prev;

  return prev[printerId] === thumbnailUrl ? prev : { ...prev, [printerId]: thumbnailUrl };
}
