import { describe, expect, it } from 'vitest';
import { computeIsSidebarOpen } from '../printerSidebarVisibility';

describe('computeIsSidebarOpen (#1702)', () => {
  // #1702: PrinterDetailsSidebar and the detailed grid's DetailedPrinterCard
  // each render their own MaterialLoadout/MmuControlBox, and MmuControlBox
  // issues fire-and-forget AMS/MMU hardware mutations with no cross-instance
  // lock. This predicate must be false whenever the detailed grid is active
  // so the two are never mounted for the same printer at once. It's tested
  // directly (rather than only through a PrintersPage render test) because a
  // React Testing Library assertion taken after an interaction can't observe
  // a regression here: `act()` (used internally by both `fireEvent` and
  // `userEvent`) flushes passive effects synchronously in jsdom, so a
  // redirect effect reacting to a wrong value here would mask the bug before
  // any DOM assertion runs.
  it('is false once a printer is expanded and viewMode is detailed', () => {
    expect(computeIsSidebarOpen('printer-1', 'detailed')).toBe(false);
  });

  it('is true when a printer is expanded and viewMode is collapsed or table', () => {
    expect(computeIsSidebarOpen('printer-1', 'collapsed')).toBe(true);
    expect(computeIsSidebarOpen('printer-1', 'table')).toBe(true);
  });

  it('is false when no printer is expanded, regardless of viewMode', () => {
    expect(computeIsSidebarOpen(null, 'collapsed')).toBe(false);
    expect(computeIsSidebarOpen(null, 'detailed')).toBe(false);
    expect(computeIsSidebarOpen(null, 'table')).toBe(false);
  });
});
