import React from 'react';
import { render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { PrinterDiscoveryModal } from '../PrinterDiscoveryModal';
import { PrinterBackend } from '@/types/api';

// Regression test for #2324: at a 375x667 mobile viewport, the discovered
// printer candidate cards used to render inside their own independently
// scrolling box (`max-h-96 overflow-y-auto`) nested inside the modal's own
// scrollable content area. Two overlapping scroll regions meant scrolling
// the modal only revealed a still-clipped, separately-scrolling results box,
// so the fixed action footer read as if it were overlapping the last
// candidate card — the first card was partly obscured and the second
// extended below the modal with no single, unobstructed way to reach it.
//
// The fix removes the nested scroll container so the results list flows as
// a normal part of Modal's own single `overflow-y-auto` content region
// (asserted here via the `modal-content` testid). jsdom performs no real
// layout/media-query evaluation, so this asserts the structural fix
// directly: the results list must not declare its own height cap or scroll
// behavior, and it must be a descendant of the modal's single scroll
// region.
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

describe('PrinterDiscoveryModal — mobile results scroll region (#2324)', () => {
  it('renders the results list as part of the modal single scroll region, not its own nested scrollbox', () => {
    render(<PrinterDiscoveryModal isOpen={true} onClose={vi.fn()} />);

    const modalContent = screen.getByTestId('modal-content');
    const resultsList = screen.getByTestId('discovery-results-list');

    // The results list must not declare a competing scroll container: no
    // fixed height cap and no independent overflow behavior. Regressing to
    // `max-h-96` / `overflow-y-auto` here recreates the nested double-scroll
    // trap that caused #2324.
    expect(resultsList.className).not.toMatch(/\bmax-h-/);
    expect(resultsList.className).not.toMatch(/\boverflow-y-auto\b/);
    expect(resultsList.className).not.toMatch(/\boverflow-auto\b/);

    // The results list — and every discovered printer card within it — must
    // live inside the modal's single scrollable content region so one
    // scroll gesture reveals every candidate above the fixed footer.
    expect(modalContent).toContainElement(resultsList);
    expect(within(modalContent).getByText('Voron V2.4')).toBeInTheDocument();
    expect(within(modalContent).getByText('Prusa MK4S')).toBeInTheDocument();

    const footer = screen.getByTestId('discovery-modal-footer');
    expect(modalContent).not.toContainElement(footer);
  });
});
