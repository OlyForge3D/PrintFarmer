import React from 'react';
import { render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { PrinterDiscoveryModal } from '../PrinterDiscoveryModal';
import { PrinterBackend } from '@/types/api';

// Regression test for #1685: at a 375px mobile viewport, once discovery
// results load, the footer row ("Close", "Add Selected", "Scan Again")
// rendered as bare children of Modal's non-wrapping `justify-end` footer
// container. Three buttons never fit in ~327px of content width, and
// because the row never wrapped, the left-most action (Close) was pushed
// off-screen instead of the right-most.
//
// The fix wraps the discovery modal's own footer buttons in a single
// container that stacks them (`flex-col`) below the `sm` (640px) breakpoint
// and only switches to the original single-row, right-aligned layout at
// `sm` and above. `flex-col` (not `flex-col-reverse`) is intentional: each
// button is an individual flex item, so a reversed column would swap
// visual stacking order without touching DOM/tab order (WCAG 2.4.3 Focus
// Order). jsdom performs no real layout/media-query evaluation (see
// DetailedPrinterCardResponsiveWidth.test.ts), so this asserts the
// structural fix directly: every footer action must be a descendant of the
// responsive wrapper, and that wrapper must declare the mobile-stack /
// desktop-row classes.

const foundPrinters = [
  {
    discoveryId: 'voron-1',
    name: 'Voron V2.4',
    backend: PrinterBackend.Moonraker,
    manufacturer: 'Voron',
    model: 'V2.4',
    discoveredAt: new Date().toISOString(),
    isReachable: true,
  },
  {
    discoveryId: 'prusa-1',
    name: 'Prusa MK4S',
    backend: PrinterBackend.PrusaLink,
    manufacturer: 'Prusa',
    model: 'MK4S',
    discoveredAt: new Date().toISOString(),
    isReachable: true,
  },
];

vi.mock('@/common/hooks/useApi', () => ({
  useStartDiscoveryStream: () => ({ mutateAsync: vi.fn(), isPending: false, error: null }),
  useCancelDiscoveryStream: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useRegisterDiscoveredPrinter: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useManufacturers: () => ({ data: [] }),
  useModels: () => ({ data: [] }),
}));

vi.mock('@/common/hooks/useSignalR', () => ({
  useSignalRConnection: () => ({ isConnected: true }),
  useDiscoveryStream: () => ({
    progress: null,
    foundPrinters,
    completed: { totalPrintersFound: foundPrinters.length },
    resetDiscovery: vi.fn(),
    isActive: false,
    isCompleted: true,
  }),
}));

describe('PrinterDiscoveryModal — mobile footer layout (#1685)', () => {
  it('keeps Close, Add Selected, and Scan Again together in a responsive stack/row wrapper', () => {
    render(
      <PrinterDiscoveryModal isOpen={true} onClose={vi.fn()} />,
    );

    const footer = screen.getByTestId('discovery-modal-footer');

    // Mobile-first: stacked full-width column in DOM order (not reversed —
    // reversing would swap visual order without touching tab order, which
    // is its own accessibility regression); only switches to a single
    // right-aligned row at the `sm` breakpoint and above.
    expect(footer.className).toMatch(/\bflex-col\b/);
    expect(footer.className).not.toMatch(/\bflex-col-reverse\b/);
    expect(footer.className).toMatch(/\bw-full\b/);
    expect(footer.className).toMatch(/\bsm:flex-row\b/);
    expect(footer.className).toMatch(/\bsm:justify-end\b/);

    // All three actions must live inside this one wrapper, not as bare
    // siblings of Modal's fixed non-wrapping footer row — that's what let
    // the left-most action get pushed off the 375px viewport.
    expect(within(footer).getByRole('button', { name: 'Close' })).toBeInTheDocument();
    expect(within(footer).getByRole('button', { name: /Add 0 Selected Printers/ })).toBeInTheDocument();
    expect(within(footer).getByRole('button', { name: 'Scan Again' })).toBeInTheDocument();

    // DOM order (== tab order) must match the pre-fix, desktop-row reading
    // order: Close, then Add Selected, then Scan Again. `flex-col` preserves
    // this; `flex-col-reverse` would not, which is exactly the regression
    // this assertion guards against.
    const buttonNames = within(footer)
      .getAllByRole('button')
      .map((button) => button.textContent);
    expect(buttonNames).toEqual(['Close', 'Add 0 Selected Printers', 'Scan Again']);
  });
});
