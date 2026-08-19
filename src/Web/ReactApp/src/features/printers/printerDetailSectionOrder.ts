/**
 * Canonical order of printer detail sections.
 *
 * `PrinterDetailsSidebar` and `DetailedPrinterCard` both surface the same
 * underlying printer detail sections (statistics, version, control, print
 * objects, movement, temperature, materials/MMU, and spool) but render them
 * with different visual layouts. To keep the two views consistent (#1698),
 * any section that appears in both components must be laid out in this
 * relative order, top to bottom.
 *
 * Note: `control` (pause/cancel/e-stop + quick access) is rendered as its
 * own collapsible section in the sidebar, but is currently embedded inside
 * the card's `move` section (via `PrinterActionBar` in
 * `MovementControlSection`'s `rightContent`). That structural difference is
 * a pre-existing divergence and is intentionally out of scope for #1698,
 * which only fixes section *order*, not visual grouping.
 *
 * Card-only elements with no sidebar equivalent (header toolbar, bed-clear
 * banner, offline troubleshooting guide, progress bar, ETA badge, failure
 * detection summary) are not part of this list and may be positioned
 * wherever makes sense for the card.
 */
export const PRINTER_DETAIL_SECTION_ORDER = [
  'statistics',
  'version',
  'control',
  'objects',
  'move',
  'temperature',
  'materials',
  'spool',
] as const;

export type PrinterDetailSection = (typeof PRINTER_DETAIL_SECTION_ORDER)[number];
