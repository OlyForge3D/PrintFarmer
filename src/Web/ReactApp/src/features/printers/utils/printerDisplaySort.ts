import { PrinterBackend, type Printer } from '@/types/api';
import { getPrinterBackendName } from '@/common/utils/enumHelpers';

export type PrinterSortMode = 'state' | 'name' | 'backend';

/**
 * Module-level collator shared by every sort comparison (#1146 item 5).
 * Creating an `Intl.Collator` has real setup cost, so building one per
 * `localeCompare` call inside an O(n log n) comparator scales badly for
 * large fleets; one instance reused across renders and sort calls keeps the
 * same default-locale ordering as the plain `String.prototype.localeCompare()`
 * calls it replaces (no options are passed, matching `localeCompare()`'s
 * defaults exactly).
 */
const PRINTER_NAME_COLLATOR = new Intl.Collator();

/** Precomputed, per-printer sort fields so the comparator does no repeated work. */
interface DecoratedPrinterForSort {
  printer: Printer;
  statePriority: number;
  backendName: string;
}

/** State priority for sorting: lower number = higher in list */
export function getStateSortPriority(printer: Printer, pendingIds: ReadonlySet<string>): number {
  if (pendingIds.has(printer.id)) return 0;       // PendingReady (attention)
  if (!printer.isOnline) return 4;                 // Offline
  const state = (printer.state || '').toLowerCase();
  if (state.includes('printing')) return 1;        // Printing
  if (state.includes('paused')) return 2;           // Paused
  return 3;                                         // Idle / other online
}

/** Helper function to get backend name from a wire or legacy value. */
export function getBackendName(backend: PrinterBackend | string | number): string {
  return getPrinterBackendName(backend);
}

/**
 * Sort printers for display (#1146 item 5). A pure, standalone function
 * (kept out of `PrintersPage.tsx` so the page module only exports the
 * component, per `react-refresh/only-export-components`) so it can also be
 * unit-tested directly. Behaviors preserved from the previous inline
 * `.sort()` call:
 *  - `printers` is never mutated; a new array is always returned (avoids the
 *    upstream-array-mutation bug where sorting the filtered list, when no
 *    filter had produced a new array, mutated `optimisticPrinters`/
 *    `displayPrinters`/the query cache in place).
 *  - Each printer's sort fields (`statePriority`, backend display name) are
 *    computed once per printer instead of once per comparator invocation,
 *    and all name comparisons share one `Intl.Collator` instead of
 *    allocating one per `localeCompare` call.
 * Sorting is stable (`Array.prototype.sort` has been spec-guaranteed stable
 * since ES2019), so ties keep their relative input order exactly as before.
 */
export function sortPrintersForDisplay(
  printers: readonly Printer[],
  sortMode: PrinterSortMode,
  pendingPrinterIds: ReadonlySet<string>,
): Printer[] {
  const decorated: DecoratedPrinterForSort[] = printers.map(printer => ({
    printer,
    statePriority: getStateSortPriority(printer, pendingPrinterIds),
    backendName: getBackendName(printer.backend),
  }));
  decorated.sort((a, b) => {
    if (sortMode === 'state') {
      if (a.statePriority !== b.statePriority) return a.statePriority - b.statePriority;
      return PRINTER_NAME_COLLATOR.compare(a.printer.name ?? '', b.printer.name ?? '');
    }
    if (sortMode === 'name') {
      return PRINTER_NAME_COLLATOR.compare(a.printer.name ?? '', b.printer.name ?? '');
    }
    if (sortMode === 'backend') {
      const cmp = PRINTER_NAME_COLLATOR.compare(a.backendName, b.backendName);
      if (cmp !== 0) return cmp;
      return PRINTER_NAME_COLLATOR.compare(a.printer.name ?? '', b.printer.name ?? '');
    }
    return 0;
  });
  return decorated.map(d => d.printer);
}