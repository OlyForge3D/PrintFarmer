import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { MaterialLoadout } from '@/features/printers/components/MaterialLoadout';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';
import type { MmuGate, MmuStatus, ToolheadDto } from '@/types/api';

const setSpool = vi.fn();
const clearSpool = vi.fn();
const coverage = vi.fn();

vi.mock('@/common/hooks/useApi', () => ({
  useSetToolheadSpool: () => ({ mutateAsync: setSpool, isPending: false }),
  useClearToolheadSpool: () => ({ mutate: clearSpool, isPending: false }),
}));

vi.mock('@/features/filament-coverage/hooks', () => ({
  usePrinterCoverageFromFleet: () => ({ data: coverage() }),
}));

vi.mock('@/features/printers/components/SpoolPickerModal', () => ({
  SpoolPickerModal: ({ onSelect }: { onSelect: (id: number) => void }) => (
    <button type="button" data-testid="spool-picker" onClick={() => onSelect(99)}>
      pick
    </button>
  ),
}));

function gate(index: number, overrides: Partial<MmuGate> = {}): MmuGate {
  return {
    index,
    status: 'Available',
    material: 'PLA',
    color: '#ff0000',
    spoolId: 0,
    ...overrides,
  } as MmuGate;
}

function mmu(gates: MmuGate[], mmuType = MmuProtocol.Qidibox): MmuStatus {
  return { enabled: true, mmuType, numGates: gates.length, gates } as MmuStatus;
}

const persistedQidiBox: ToolheadDto[] = [
  { id: 'th-0', index: 0, name: 'Hotend', toolheadType: 'Physical' } as ToolheadDto,
  { id: 'th-1', index: 1, name: 'Gate 1', toolheadType: 'MmuGate' } as ToolheadDto,
  { id: 'th-2', index: 2, name: 'Gate 2', toolheadType: 'MmuGate' } as ToolheadDto,
  { id: 'th-3', index: 3, name: 'Gate 3', toolheadType: 'MmuGate' } as ToolheadDto,
];

function renderLoadout(props: Partial<React.ComponentProps<typeof MaterialLoadout>> = {}) {
  return render(
    <MaterialLoadout
      printerId="printer-1"
      mmuStatus={mmu([gate(0), gate(1), gate(2), gate(3)])}
      toolheads={persistedQidiBox}
      reviewedRowVersion="rev-1"
      {...props}
    />,
  );
}

describe('MaterialLoadout', () => {
  beforeEach(() => {
    setSpool.mockReset().mockResolvedValue(undefined);
    clearSpool.mockReset();
    coverage.mockReset().mockReturnValue(undefined);
  });

  it('renders one slot per physical filament position', () => {
    renderLoadout();

    const rail = screen.getByTestId('material-loadout');
    expect(within(rail).getAllByRole('button')).toHaveLength(4);
    expect(screen.getByTestId('loadout-slot-3')).toBeInTheDocument();
  });

  it('shows a single detail panel instead of one card per slot', () => {
    renderLoadout();

    expect(screen.queryByTestId('loadout-drawer')).not.toBeInTheDocument();

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    expect(screen.getAllByTestId('loadout-drawer')).toHaveLength(1);

    fireEvent.click(screen.getByTestId('loadout-slot-2'));
    expect(screen.getAllByTestId('loadout-drawer')).toHaveLength(1);
  });

  it('collapses the detail panel when the selected slot is clicked again', () => {
    renderLoadout();

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    fireEvent.click(screen.getByTestId('loadout-slot-1'));

    expect(screen.queryByTestId('loadout-drawer')).not.toBeInTheDocument();
  });

  it('assigns a spool to the persisted index of the slot the user clicked', async () => {
    // Live gate 2 is persisted as toolhead 3; sending the live index would have
    // written the spool to the neighbouring gate.
    renderLoadout();

    fireEvent.click(screen.getByTestId('loadout-slot-2'));
    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    expect(setSpool).toHaveBeenCalledWith({
      printerId: 'printer-1',
      toolheadIndex: 3,
      spoolId: 99,
      reviewedRowVersion: 'rev-1',
    });
  });

  it('clears a spool from the persisted index of the slot the user clicked', () => {
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1, { spoolId: 12 }), gate(2), gate(3)]),
    });

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));

    expect(clearSpool).toHaveBeenCalledWith({
      printerId: 'printer-1',
      toolheadIndex: 2,
      reviewedRowVersion: 'rev-1',
    });
  });

  it('refuses to mutate without the printer revision', () => {
    renderLoadout({ reviewedRowVersion: undefined });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));

    expect(screen.queryByTestId('spool-picker')).not.toBeInTheDocument();
    expect(setSpool).not.toHaveBeenCalled();
  });

  it('joins coverage on the g-code index so figures land on the right slot', () => {
    coverage.mockReturnValue({
      printerId: 'printer-1',
      printerName: 'Qidi Plus 4',
      status: 'runout',
      toolheads: [
        { toolheadIndex: 2, status: 'runout', remainingGrams: 100, totalDemandGrams: 400 },
      ],
    });

    renderLoadout();

    expect(screen.getByTestId('loadout-slot-2')).toHaveAttribute('data-status', 'runout');
    expect(screen.getByTestId('loadout-slot-1')).toHaveAttribute('data-status', 'unknown');

    fireEvent.click(screen.getByTestId('loadout-slot-2'));
    const drawer = screen.getByTestId('loadout-drawer');
    expect(within(drawer).getByText('100g left')).toBeInTheDocument();
    expect(within(drawer).getByText('400g needed')).toBeInTheDocument();
  });

  it('calls a toolchanger slot a tool, never an AMS gate', () => {
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1)], MmuProtocol.SnapmakerU1),
      toolheads: [persistedQidiBox[0]],
    });

    expect(screen.getByText('Toolheads')).toBeInTheDocument();
    expect(screen.queryByText(/AMS/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    const drawer = screen.getByTestId('loadout-drawer');
    expect(within(drawer).getByText('T1')).toBeInTheDocument();
    expect(within(drawer).queryByText('Gate')).not.toBeInTheDocument();
  });

  it('sends the unshifted index on a toolchanger', () => {
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1)], MmuProtocol.SnapmakerU1),
      toolheads: [persistedQidiBox[0]],
    });

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
    fireEvent.click(screen.getByTestId('spool-picker'));

    expect(setSpool).toHaveBeenCalledWith(
      expect.objectContaining({ toolheadIndex: 1 }),
    );
  });

  it('renders nothing for a printer with a single filament source', () => {
    const { container } = render(
      <MaterialLoadout printerId="printer-1" toolheads={[persistedQidiBox[0]]} />,
    );

    expect(container).toBeEmptyDOMElement();
  });
});
