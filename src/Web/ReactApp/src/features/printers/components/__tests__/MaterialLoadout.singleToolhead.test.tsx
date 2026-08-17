/**
 * Verifies that single-toolhead printers render through the MaterialLoadout rail
 * and that FilamentCoverageBreakdown does NOT appear on that surface.
 *
 * This is the user-visible outcome of #1665: the green "Filament OK" banner
 * must not render for single-toolhead non-AMS printers — they get the rail.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MaterialLoadout } from '@/features/printers/components/MaterialLoadout';
import type { ToolheadDto } from '@/types/api';

vi.mock('@/common/hooks/useApi', () => ({
  useSetToolheadSpool: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useClearToolheadSpool: () => ({ mutateAsync: vi.fn(), isPending: false }),
  usePrinterDetails: () => ({ data: undefined }),
}));

vi.mock('@/features/filament-coverage/hooks', () => ({
  usePrinterCoverageFromFleet: () => ({ data: undefined }),
}));

// Do NOT mock FilamentCoverageBreakdown — we want to assert it's absent.
vi.mock('@/features/printers/components/SpoolPickerModal', () => ({
  SpoolPickerModal: () => null,
}));

const singleToolhead: ToolheadDto[] = [
  { id: 'th-0', index: 0, name: 'Hotend', toolheadType: 'Physical', currentMaterial: 'PLA', currentFilamentColor: '#00ff00' } as ToolheadDto,
];

describe('MaterialLoadout — single-toolhead printer (#1665)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders the material loadout rail for a single-toolhead printer', () => {
    render(
      <MaterialLoadout
        printerId="printer-1"
        toolheads={singleToolhead}
        reviewedRowVersion="rev-1"
      />,
    );

    expect(screen.getByTestId('material-loadout')).toBeInTheDocument();
    expect(screen.getByTestId('loadout-slot-0')).toBeInTheDocument();
  });

  it('does not render FilamentCoverageBreakdown within the loadout rail', () => {
    const { container } = render(
      <MaterialLoadout
        printerId="printer-1"
        toolheads={singleToolhead}
        reviewedRowVersion="rev-1"
      />,
    );

    // FilamentCoverageBreakdown is never part of MaterialLoadout — but verify
    // there is no "Filament OK" or coverage-breakdown content inside the rail.
    expect(container.querySelector('[data-testid="filament-coverage-breakdown"]')).toBeNull();
    // The component renders a single-slot rail, confirming it does NOT return
    // null (which is what would make the sidebar fall through to the legacy section).
    expect(screen.getByTestId('material-loadout')).toBeInTheDocument();
  });

  it('renders zero-toolhead printer with currentSpoolId through the rail', () => {
    // Zero toolheads (or undefined) + currentSpoolId must synthesize a T0 slot.
    // Before the fix, the component's internal resolver didn't receive
    // currentSpoolId, so it returned null and rendered nothing.
    render(
      <MaterialLoadout
        printerId="printer-1"
        toolheads={[]}
        currentSpoolId={42}
        reviewedRowVersion="rev-1"
      />,
    );

    expect(screen.getByTestId('material-loadout')).toBeInTheDocument();
    expect(screen.getByTestId('loadout-slot-0')).toBeInTheDocument();
  });

  it('renders zero-toolhead printer with undefined toolheads + currentSpoolId', () => {
    render(
      <MaterialLoadout
        printerId="printer-1"
        toolheads={undefined}
        currentSpoolId={7}
        reviewedRowVersion="rev-1"
      />,
    );

    expect(screen.getByTestId('material-loadout')).toBeInTheDocument();
    expect(screen.getByTestId('loadout-slot-0')).toBeInTheDocument();
  });
});
