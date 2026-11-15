import { PrinterBackend, type Printer } from '@/types/api';
import { getPrinterBackendName } from '@/common/utils/enumHelpers';
import type { SortDirection } from '@/common/hooks/useTableSort';

export type PrinterSortMode = 'state' | 'name' | 'backend';

/**
 * Module-level collator shared by every sort comparison (#1146 item 5).
 * Creating an `Intl.Collator` has real setup cost, so building one per
 * `localeCompare` call inside an O(n log n) comparator scales badly for
 * large fleets; one instance reused across renders and sort calls keeps the
 * locale-aware, case-insensitive ordering across card and table views.
 */
const PRINTER_NAME_COLLATOR = new Intl.Collator(undefined, { sensitivity: 'accent' });

/** Precomputed, per-printer sort fields so the comparator does no repeated work. */
interface DecoratedPrinterForSort {
  printer: Printer;
  statePriority: number;
  backendName: string;
}

/** State priority for sorting: lower number = higher in list */
export function getStateSortPriority(printer: Printer, pendingIds: ReadonlySet<string>): number {
  if (!printer.isOnline) return 5;
  if (pendingIds.has(printer.id)) return 1;
  const state = (printer.state || '').toLowerCase();
  if (state === 'printing' || state === 'paused') return 0;
  if (printer.inMaintenance) return 4;
  if (state === 'error') return 2;
  return 3;
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
 * Names compare case-insensitively; IDs make equal-name ordering deterministic.
 */
export function sortPrintersForDisplay(
  printers: readonly Printer[],
  sortMode: PrinterSortMode,
  pendingPrinterIds: ReadonlySet<string>,
  direction: SortDirection = 'asc',
): Printer[] {
  const activeMode = direction === null ? 'state' : sortMode;
  const multiplier = direction === 'desc' ? -1 : 1;
  const compareNames = (a: Printer, b: Printer) =>
    PRINTER_NAME_COLLATOR.compare(a.name ?? '', b.name ?? '')
      || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
  const decorated: DecoratedPrinterForSort[] = printers.map(printer => ({
    printer,
    statePriority: getStateSortPriority(printer, pendingPrinterIds),
    backendName: getBackendName(printer.backend),
  }));
  decorated.sort((a, b) => {
    if (activeMode === 'state') {
      if (a.statePriority !== b.statePriority) return multiplier * (a.statePriority - b.statePriority);
      return compareNames(a.printer, b.printer);
    }
    if (activeMode === 'name') {
      return multiplier * compareNames(a.printer, b.printer);
    }
    if (activeMode === 'backend') {
      const cmp = PRINTER_NAME_COLLATOR.compare(a.backendName, b.backendName);
      if (cmp !== 0) return multiplier * cmp;
      return compareNames(a.printer, b.printer);
    }
    return 0;
  });
  return decorated.map(d => d.printer);
}