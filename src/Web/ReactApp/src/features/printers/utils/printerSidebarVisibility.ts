import type { ViewMode } from '@/common/components/ViewModeToggle';

/**
 * #1702: whether the printer-details sidebar (which renders its own
 * `MaterialLoadout` and `MmuControlBox`) should be shown for the printer
 * expanded via route. This must be computed at render time — not just
 * eventually corrected by an effect — because `viewMode` can change
 * synchronously on the client (via `ViewModeToggle` or the `v` keyboard
 * shortcut) without a route change. If the sidebar's visibility depended on
 * `expandedPrinterId` alone, it would still mount alongside the detailed
 * grid's `DetailedPrinterCard` for the same printer — each with their own
 * `MmuControlBox` — for the render that flips `viewMode` to `'detailed'`,
 * until a redirect effect reacted a tick later. In a real browser that
 * render still paints, visible and clickable, before any effect can run.
 *
 * Kept as a standalone, directly-unit-testable pure function rather than an
 * inline expression in `PrintersPage`: a React Testing Library render test
 * cannot observe a regression here, because `act()` (used internally by both
 * `fireEvent` and `userEvent`) flushes passive effects synchronously in
 * jsdom, masking the very race this guards against from any DOM assertion
 * taken after an interaction.
 */
export function computeIsSidebarOpen(expandedPrinterId: string | null, viewMode: ViewMode): boolean {
  return !!expandedPrinterId && viewMode !== 'detailed';
}
