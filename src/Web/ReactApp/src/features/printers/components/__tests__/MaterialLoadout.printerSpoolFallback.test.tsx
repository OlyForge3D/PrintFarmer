import { describe, it, expect, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MaterialLoadout } from '@/features/printers/components/MaterialLoadout';
import type { MmuStatus, PrinterSpoolInfo, ToolheadDto } from '@/types/api';

const mutations = vi.hoisted(() => ({
  set: vi.fn(),
  clearToolhead: vi.fn(),
  clearActive: vi.fn(),
  setActive: vi.fn(),
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));
vi.mock('@/features/printers/hooks/usePrinterSpoolInventory', () => ({
  usePrinterSpoolInventory: () => ({ data: undefined, isError: false, refetch: vi.fn() }),
}));
vi.mock('@/common/hooks/useApi', () => ({
  useSetToolheadSpool: () => ({ mutateAsync: mutations.set, isPending: false }),
  useClearToolheadSpool: () => ({ mutateAsync: mutations.clearToolhead, isPending: false }),
  useSetActiveSpool: () => ({ mutateAsync: mutations.setActive, isPending: false }),
  useClearActiveSpool: () => ({ mutateAsync: mutations.clearActive, isPending: false }),
  usePrinterDetails: () => ({ data: undefined }),
}));
vi.mock('@/features/filament-coverage/hooks', () => ({
  usePrinterCoverageFromFleet: () => ({ data: undefined }),
}));
vi.mock('@/features/printers/components/SpoolPickerModal', () => ({
  SpoolPickerModal: ({ onSelect }: { onSelect: (id: number) => void }) => (
    <button type="button" data-testid="spool-picker" onClick={() => onSelect(99)}>
      pick
    </button>
  ),
}));

const pa12: PrinterSpoolInfo = {
  hasActiveSpool: true,
  activeSpoolId: 21,
  material: 'PA12',
  filamentName: 'PA12-CF black',
  colorHex: '#111111',
  remainingWeightG: 1000,
};

const bareToolhead = { id: 'th-0', index: 0, name: 'Hotend', toolheadType: 'Physical' } as ToolheadDto;

describe('MaterialLoadout — printer-level spool fallback', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mutations.clearActive.mockResolvedValue('rev-2');
    mutations.clearToolhead.mockResolvedValue('rev-2');
    mutations.setActive.mockResolvedValue('rev-2');
    mutations.set.mockResolvedValue('rev-2');
  });

  it('changes a printer-level spool through the printer scalar, not setToolheadSpool', async () => {
    render(
      <MaterialLoadout
        printerId="p1"
        toolheads={[bareToolhead]}
        currentSpoolId={21}
        spoolInfo={pa12}
        reviewedRowVersion="rev-1"
      />,
    );

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    fireEvent.click(screen.getByTestId('slot-action-change'));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() =>
      expect(mutations.setActive).toHaveBeenCalledWith({ printerId: 'p1', spoolId: 99, reviewedRowVersion: 'rev-1' }),
    );
    expect(mutations.set).not.toHaveBeenCalled();
  });

  it('still assigns to a real toolhead through setToolheadSpool when it holds its own spool', async () => {
    render(
      <MaterialLoadout
        printerId="p1"
        toolheads={[{ ...bareToolhead, currentSpoolId: 7 } as ToolheadDto]}
        currentSpoolId={21}
        spoolInfo={pa12}
        reviewedRowVersion="rev-1"
      />,
    );

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    fireEvent.click(screen.getByTestId('slot-action-change'));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() => expect(mutations.set).toHaveBeenCalledTimes(1));
    expect(mutations.setActive).not.toHaveBeenCalled();
  });

  it('does not apply the printer-level spool to a Snapmaker U1 reporting empty gates', () => {
    const u1Status = { enabled: true, mmuType: 'SnapmakerU1', numGates: 0, gates: [] } as unknown as MmuStatus;
    render(
      <MaterialLoadout
        printerId="p1"
        mmuStatus={u1Status}
        toolheads={[bareToolhead]}
        currentSpoolId={21}
        spoolInfo={pa12}
        reviewedRowVersion="rev-1"
      />,
    );

    const slot = screen.getByTestId('loadout-slot-0');
    expect(slot).toHaveAttribute('data-state', 'none');
    expect(slot).not.toHaveTextContent('PA12');
    expect(within(slot).getByTestId('slot-badge-assign')).toBeInTheDocument();
  });

  it('shows material, grams and color for a persisted toolhead row without spool data', () => {
    render(
      <MaterialLoadout
        printerId="p1"
        toolheads={[bareToolhead]}
        currentSpoolId={21}
        spoolInfo={pa12}
        reviewedRowVersion="rev-1"
      />,
    );

    const slot = screen.getByTestId('loadout-slot-0');
    expect(slot).toHaveAttribute('data-state', 'assigned');
    expect(within(slot).getByTestId('filament-summary-name')).toHaveTextContent('PA12-CF black');
    expect(within(slot).getByTestId('filament-grams')).toHaveTextContent('1000 g');
    expect(within(slot).queryByTestId('slot-badge-assign')).not.toBeInTheDocument();

    fireEvent.click(slot);
    expect(within(screen.getByTestId('slot-panel')).getByText('Spool #21')).toBeInTheDocument();
  });

  it('keeps an empty slot when the printer reports no active spool', () => {
    render(
      <MaterialLoadout
        printerId="p1"
        toolheads={[bareToolhead]}
        spoolInfo={{ ...pa12, hasActiveSpool: false }}
        reviewedRowVersion="rev-1"
      />,
    );

    const slot = screen.getByTestId('loadout-slot-0');
    expect(slot).toHaveAttribute('data-state', 'none');
    expect(within(slot).getByTestId('slot-badge-assign')).toBeInTheDocument();
  });

  it('unassigns a printer-level spool through the printer scalar, not toolhead 0', async () => {
    render(
      <MaterialLoadout
        printerId="p1"
        toolheads={[bareToolhead]}
        currentSpoolId={21}
        spoolInfo={pa12}
        reviewedRowVersion="rev-1"
      />,
    );

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    fireEvent.click(screen.getByRole('button', { name: 'Unassign spool' }));

    await waitFor(() => expect(mutations.clearActive).toHaveBeenCalledWith({ printerId: 'p1', reviewedRowVersion: 'rev-1' }));
    expect(mutations.clearToolhead).not.toHaveBeenCalled();
  });

  it('unassigns the synthesized legacy T0 through the printer scalar', async () => {
    render(
      <MaterialLoadout
        printerId="p1"
        toolheads={[]}
        currentSpoolId={21}
        spoolInfo={pa12}
        reviewedRowVersion="rev-1"
      />,
    );

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    fireEvent.click(screen.getByRole('button', { name: 'Unassign spool' }));

    await waitFor(() => expect(mutations.clearActive).toHaveBeenCalledTimes(1));
    expect(mutations.clearToolhead).not.toHaveBeenCalled();
  });

  it('is empty again after the spool is unassigned', () => {
    const { rerender } = render(
      <MaterialLoadout printerId="p1" toolheads={[]} currentSpoolId={21} spoolInfo={pa12} reviewedRowVersion="rev-1" />,
    );
    expect(screen.getByTestId('loadout-slot-0')).toHaveAttribute('data-state', 'assigned');

    rerender(
      <MaterialLoadout printerId="p1" toolheads={[]} currentSpoolId={null} spoolInfo={{ hasActiveSpool: false }} reviewedRowVersion="rev-2" />,
    );
    const slot = screen.getByTestId('loadout-slot-0');
    expect(slot).toHaveAttribute('data-state', 'none');
    expect(slot).not.toHaveTextContent('PA12');
  });

  it('does not apply printer-level data to a multi-toolhead printer', () => {
    render(
      <MaterialLoadout
        printerId="p1"
        toolheads={[bareToolhead, { ...bareToolhead, id: 'th-1', index: 1 }]}
        currentSpoolId={21}
        spoolInfo={pa12}
        reviewedRowVersion="rev-1"
      />,
    );

    expect(screen.getByTestId('loadout-slot-0')).toHaveAttribute('data-state', 'none');
    expect(screen.getByTestId('loadout-slot-1')).toHaveAttribute('data-state', 'none');
    expect(screen.queryByText(/PA12/)).not.toBeInTheDocument();
  });
});
