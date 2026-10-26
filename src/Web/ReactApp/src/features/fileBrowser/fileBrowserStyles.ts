/**
 * Shared presentation classes for compact file listings. The local library (FileBrowser
 * with `compact`) and the printer-resident catalog use the same values so both sources
 * render identical card grids and 12px tables.
 */
export const COMPACT_FILE_GRID_CLASS = 'grid gap-3 grid-cols-[repeat(auto-fill,minmax(9.5rem,1fr))]';
export const COMPACT_FILE_TABLE_CLASS = 'w-full text-xs border-collapse';
export const FILE_TABLE_HEADER_CELL_CLASS = 'px-3 py-2 text-left font-semibold text-pf-text-secondary text-xs uppercase tracking-wider';
export const COMPACT_FILE_TABLE_CELL_CLASS = 'px-3 py-1.5 align-middle text-pf-text-primary';
