import React from 'react';
import { render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AddPrinterModal } from '../AddPrinterModal';

// Regression test for #1820: at a 320px mobile viewport, the Add Printer
// modal footer's Cancel, Test, and Add Printer buttons rendered as bare
// children of a single non-wrapping `flex` row, with Cancel and Add Printer
// set to `flex-1` (grow-to-fill, not shrink-below-content). The three
// buttons' combined min-content width never fit in ~240px of footer content
// width, and because Modal's fixed footer container is `justify-end`
// (right-aligned), the overflow bled off the *left* edge instead of the
// right — pushing Cancel almost entirely out of the viewport
// (approx x=-46.6 through x=33.9) and out of reach for mouse and keyboard
// users alike.
//
// The fix mirrors the established remedy for the same class of bug in
// PrinterDiscoveryModal (#1685): wrap the footer actions in a single
// container that stacks them (`flex-col`, not `flex-col-reverse` — a
// reversed column would swap visual order without touching DOM/tab order,
// its own WCAG 2.4.3 Focus Order regression) below the `sm` (640px)
// breakpoint, and only switch to a single right-aligned, wrapping row at
// `sm` and above. jsdom performs no real layout/media-query evaluation, so
// this asserts the structural fix directly: every footer action must be a
// descendant of the responsive wrapper, and that wrapper must declare the
// mobile-stack / desktop-row classes.

vi.mock('@/common/hooks/useApi', () => ({
  useManufacturers: () => ({ data: [], isLoading: false, error: null }),
  useModels: () => ({ data: [], isLoading: false, error: null }),
}));

describe('AddPrinterModal — mobile footer layout (#1820)', () => {
  it('keeps Cancel, Test, and Add Printer together in a responsive stack/row wrapper', () => {
    render(
      <AddPrinterModal isOpen={true} onClose={vi.fn()} onSuccess={vi.fn()} />,
    );

    const footer = screen.getByTestId('add-printer-modal-footer');

    // Mobile-first: stacked full-width column in DOM order (not reversed —
    // reversing would swap visual order without touching tab order, which
    // is its own accessibility regression); only switches to a single
    // right-aligned, wrapping row at the `sm` breakpoint and above.
    expect(footer.className).toMatch(/\bflex-col\b/);
    expect(footer.className).not.toMatch(/\bflex-col-reverse\b/);
    expect(footer.className).toMatch(/\bw-full\b/);
    expect(footer.className).toMatch(/\bsm:flex-row\b/);
    expect(footer.className).toMatch(/\bsm:flex-wrap\b/);
    expect(footer.className).toMatch(/\bsm:justify-end\b/);

    // All three actions must live inside this one wrapper, not as bare
    // siblings of Modal's fixed non-wrapping footer row — that's what let
    // Cancel get pushed off the 320px viewport.
    expect(within(footer).getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
    expect(within(footer).getByRole('button', { name: 'Test' })).toBeInTheDocument();
    expect(within(footer).getByRole('button', { name: 'Add Printer' })).toBeInTheDocument();

    // DOM order (== tab order) must match the pre-fix, desktop-row reading
    // order: Cancel, then Test, then Add Printer. `flex-col` preserves
    // this; `flex-col-reverse` would not, which is exactly the regression
    // this assertion guards against.
    const buttonNames = within(footer)
      .getAllByRole('button')
      .map((button) => button.textContent);
    expect(buttonNames).toEqual(['Cancel', 'Test', 'Add Printer']);
  });
});
