/**
 * Sort printers: online first, then alphabetically by name within each group.
 * Returns a new sorted array (does not mutate the input).
 */
export function sortPrintersByAvailability<T extends { isOnline?: boolean; name?: string }>(
  printers: T[],
): T[] {
  return [...printers].sort((a, b) => {
    const aOnline = a.isOnline ?? false;
    const bOnline = b.isOnline ?? false;
    if (aOnline !== bOnline) return aOnline ? -1 : 1;
    return (a.name ?? '').localeCompare(b.name ?? '');
  });
}
