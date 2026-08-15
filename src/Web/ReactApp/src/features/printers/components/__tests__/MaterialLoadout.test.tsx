import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { MaterialLoadout } from '@/features/printers/components/MaterialLoadout';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';
import type { MmuGate, MmuStatus, ToolheadDto } from '@/types/api';
import { MmuGateStatus } from '@/types/api';

const setSpool = vi.fn();
const clearSpool = vi.fn();
const coverage = vi.fn();

vi.mock('@/common/hooks/useApi', () => ({
  useSetToolheadSpool: () => ({ mutateAsync: setSpool, isPending: false }),
  useClearToolheadSpool: () => ({ mutateAsync: clearSpool, isPending: false }),
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
    status: MmuGateStatus.Available,
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
  { id: 'th-4', index: 4, name: 'Gate 4', toolheadType: 'MmuGate' } as ToolheadDto,
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
    clearSpool.mockReset().mockResolvedValue(undefined);
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

    await waitFor(() =>
      expect(setSpool).toHaveBeenCalledWith({
        printerId: 'printer-1',
        toolheadIndex: 3,
        spoolId: 99,
        reviewedRowVersion: 'rev-1',
      }),
    );
  });

  it('assigns through non-contiguous persisted gate ordering without offset inference', async () => {
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1)]),
      toolheads: [
        { id: 'th-0', index: 0, name: 'Hotend', toolheadType: 'Physical' } as ToolheadDto,
        { id: 'th-2', index: 2, name: 'Gate A', toolheadType: 'MmuGate' } as ToolheadDto,
        { id: 'th-5', index: 5, name: 'Gate B', toolheadType: 'MmuGate' } as ToolheadDto,
      ],
    });

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() =>
      expect(setSpool).toHaveBeenCalledWith({
        printerId: 'printer-1',
        toolheadIndex: 5,
        spoolId: 99,
        reviewedRowVersion: 'rev-1',
      }),
    );
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

  it('blocks assignment up front when the printer revision is unavailable', () => {
    renderLoadout({ reviewedRowVersion: undefined });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    const assign = screen.getByRole('button', { name: 'Assign' });

    expect(assign).toBeDisabled();
    expect(screen.getByText(/Printer revision unavailable/)).toBeInTheDocument();

    fireEvent.click(assign);
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

  it('sends the unshifted index on a toolchanger', async () => {
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1)], MmuProtocol.SnapmakerU1),
      toolheads: [persistedQidiBox[0]],
    });

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
    fireEvent.click(screen.getByTestId('spool-picker'));

    await waitFor(() =>
      expect(setSpool).toHaveBeenCalledWith(
        expect.objectContaining({ toolheadIndex: 1 }),
      ),
    );
  });

  it('renders nothing for a printer with a single filament source', () => {
    const { container } = render(
      <MaterialLoadout printerId="printer-1" toolheads={[persistedQidiBox[0]]} />,
    );

    expect(container).toBeEmptyDOMElement();
  });

  it('gates spool mutation on persisted topology for a live-MMU printer (blocker 2)', () => {
    // Live MMU status arrives via SignalR before the persisted topology fetch
    // resolves. Without topology, mapping live G1 to the persisted index is a
    // guess and could write G1 to physical hotend index 0. Assignment must be
    // blocked with an explanatory hint until topology arrives.
    renderLoadout({ toolheads: undefined });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    const assign = screen.getByRole('button', { name: 'Assign' });

    expect(assign).toBeDisabled();
    expect(
      screen.getByText(/Materials topology not yet loaded/),
    ).toBeInTheDocument();

    fireEvent.click(assign);
    expect(screen.queryByTestId('spool-picker')).not.toBeInTheDocument();
    expect(setSpool).not.toHaveBeenCalled();
  });

  it('does not accept physical-only persisted topology or target hotend index 0', () => {
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1)]),
      toolheads: [
        { id: 'th-0', index: 0, name: 'Hotend', toolheadType: 'Physical' } as ToolheadDto,
        { id: 'th-1', index: 1, name: 'Second hotend', toolheadType: 'Physical' } as ToolheadDto,
      ],
    });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    const assign = screen.getByRole('button', { name: 'Assign' });

    expect(assign).toBeDisabled();
    expect(screen.getByText(/Materials topology not yet loaded/)).toBeInTheDocument();
    fireEvent.click(assign);
    expect(setSpool).not.toHaveBeenCalled();
  });

  it('keeps external hotend testid distinct from the first MMU gate at G-code 0 (blocker 5)', () => {
    // Persisted-only fallback with an external hotend at physical index 0 and
    // gates starting at persisted index 1 (gcode 0). The two rows must remain
    // distinguishable via testids and data-source, and only the gate row must
    // receive the g-code-indexed coverage entry.
    coverage.mockReturnValue({
      printerId: 'printer-1',
      printerName: 'Qidi Plus 4',
      status: 'runout',
      toolheads: [
        { toolheadIndex: 0, status: 'runout', remainingGrams: 50, totalDemandGrams: 400 },
      ],
    });

    render(
      <MaterialLoadout
        printerId="printer-1"
        toolheads={[
          {
            id: 'th-0',
            index: 0,
            name: 'Hotend',
            toolheadType: 'Physical',
            currentMaterial: 'ASA',
            currentSpoolId: 9,
          } as ToolheadDto,
          { id: 'th-1', index: 1, name: 'Gate 1', toolheadType: 'MmuGate' } as ToolheadDto,
          { id: 'th-2', index: 2, name: 'Gate 2', toolheadType: 'MmuGate' } as ToolheadDto,
        ]}
        reviewedRowVersion="rev-1"
      />,
    );

    const gateSlot = screen.getByTestId('loadout-slot-0');
    const externalSlot = screen.getByTestId('loadout-slot-external-0');
    expect(gateSlot).toHaveAttribute('data-source', 'gate');
    expect(externalSlot).toHaveAttribute('data-source', 'external');
    // Only the gate row must inherit the g-code 0 coverage entry — the
    // external must not pick up the gate's remaining-material figures.
    expect(gateSlot).toHaveAttribute('data-status', 'runout');
    expect(externalSlot).toHaveAttribute('data-status', 'unknown');
  });

  it('does not crash when coverage arrives without a toolheads array', () => {
    // A partial/degraded coverage payload must not take the whole printer card
    // down with it — `toolheads` is optional on the wire.
    coverage.mockReturnValue({
      printerId: 'printer-1',
      printerName: 'Qidi Plus 4',
      status: 'unknown',
    });

    expect(() => renderLoadout()).not.toThrow();
    expect(screen.getByTestId('material-loadout')).toBeInTheDocument();
  });

  it('presents a device-disabled gate as unassignable', () => {
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1, { status: MmuGateStatus.Disabled }), gate(2), gate(3)]),
    });

    const disabledSlot = screen.getByTestId('loadout-slot-1');
    expect(disabledSlot).toHaveAttribute('data-disabled', 'true');
    expect(screen.getByTestId('loadout-slot-0')).not.toHaveAttribute('data-disabled');

    fireEvent.click(disabledSlot);
    const drawer = screen.getByTestId('loadout-drawer');
    expect(within(drawer).getByText('Disabled')).toBeInTheDocument();

    const assign = screen.getByRole('button', { name: 'Assign' });
    expect(assign).toBeDisabled();
    fireEvent.click(assign);
    expect(screen.queryByTestId('spool-picker')).not.toBeInTheDocument();
    expect(setSpool).not.toHaveBeenCalled();
  });

  it('never marks a toolchanger tool disabled from MMU gate status', () => {
    // A toolchanger reports real toolheads over the MMU channel; the Happy Hare
    // gate-status vocabulary does not apply to them.
    renderLoadout({
      mmuStatus: mmu(
        [gate(0), gate(1, { status: MmuGateStatus.Disabled })],
        MmuProtocol.SnapmakerU1,
      ),
      toolheads: [persistedQidiBox[0]],
    });

    expect(screen.getByTestId('loadout-slot-1')).not.toHaveAttribute('data-disabled');
  });

  it('writes against the revision reviewed when the slot was opened, not a newer one', async () => {
    // A SignalR `printerupdated` can land while the drawer is open. The user
    // chose their spool against the older state, so the write must still be
    // validated against that revision and 412 — silently adopting the fresh
    // revision would overwrite whatever changed underneath.
    const { rerender } = render(
      <MaterialLoadout
        printerId="printer-1"
        mmuStatus={mmu([gate(0), gate(1), gate(2), gate(3)])}
        toolheads={persistedQidiBox}
        reviewedRowVersion="rev-1"
      />,
    );

    fireEvent.click(screen.getByTestId('loadout-slot-2'));

    rerender(
      <MaterialLoadout
        printerId="printer-1"
        mmuStatus={mmu([gate(0), gate(1), gate(2), gate(3)])}
        toolheads={persistedQidiBox}
        reviewedRowVersion="rev-2"
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() =>
      expect(setSpool).toHaveBeenCalledWith(
        expect.objectContaining({ reviewedRowVersion: 'rev-1' }),
      ),
    );
  });

  it('re-anchors the revision when a different slot is opened', async () => {
    const { rerender } = render(
      <MaterialLoadout
        printerId="printer-1"
        mmuStatus={mmu([gate(0), gate(1), gate(2), gate(3)])}
        toolheads={persistedQidiBox}
        reviewedRowVersion="rev-1"
      />,
    );

    fireEvent.click(screen.getByTestId('loadout-slot-2'));
    rerender(
      <MaterialLoadout
        printerId="printer-1"
        mmuStatus={mmu([gate(0), gate(1), gate(2), gate(3)])}
        toolheads={persistedQidiBox}
        reviewedRowVersion="rev-2"
      />,
    );
    // Opening a slot again means the user has now reviewed the newer state.
    fireEvent.click(screen.getByTestId('loadout-slot-3'));

    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() =>
      expect(setSpool).toHaveBeenCalledWith(
        expect.objectContaining({ reviewedRowVersion: 'rev-2' }),
      ),
    );
  });

  it('keeps the drawer open when an assignment fails', async () => {
    setSpool.mockRejectedValue(new Error('412'));
    renderLoadout();

    fireEvent.click(screen.getByTestId('loadout-slot-2'));
    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() => expect(setSpool).toHaveBeenCalled());
    // The picker stays mounted so the user can retry rather than losing context.
    expect(screen.getByTestId('spool-picker')).toBeInTheDocument();
    expect(screen.getByTestId('loadout-drawer')).toBeInTheDocument();
  });

  // ── Behaviour inherited from the retired AmsSlotVisualization suite ──

  it('reports how many slots carry filament', () => {
    renderLoadout({
      mmuStatus: mmu([
        gate(0, { material: 'PLA' }),
        gate(1, { material: undefined, spoolId: 0 }),
        gate(2, { material: undefined, spoolId: 0 }),
        gate(3, { material: undefined, spoolId: 0 }),
      ]),
    });

    expect(screen.getByText(/1\s*\/\s*4/)).toBeInTheDocument();
  });

  it('describes an empty slot as empty rather than showing a stale material', () => {
    renderLoadout({
      mmuStatus: mmu([gate(0, { material: undefined, spoolId: 0 }), gate(1), gate(2), gate(3)]),
    });

    expect(
      screen.getByRole('button', { name: /G1 gate, empty/i }),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    expect(
      within(screen.getByTestId('loadout-drawer')).getByText('No spool assigned'),
    ).toBeInTheDocument();
  });

  it('exposes the device-reported slot name without crowding the rail', async () => {
    renderLoadout({
      mmuStatus: mmu([gate(0, { name: 'Left feeder' }), gate(1), gate(2), gate(3)]),
    });

    // The rail keeps the terse label; the full device name rides along as a
    // tooltip so a 4-slot strip stays scannable.
    const slot = screen.getByTestId('loadout-slot-0');
    expect(within(slot).getByText('G1')).toBeInTheDocument();
    expect(screen.queryByText('Left feeder')).not.toBeInTheDocument();

    fireEvent.mouseEnter(slot.parentElement as HTMLElement);
    expect(await screen.findByRole('tooltip')).toHaveTextContent('Left feeder');
  });

  it('does not wrap a slot in a tooltip when the device name adds nothing', () => {
    renderLoadout({ mmuStatus: mmu([gate(0, { name: 'G1' }), gate(1), gate(2), gate(3)]) });

    fireEvent.mouseEnter(
      screen.getByTestId('loadout-slot-0').parentElement as HTMLElement,
    );
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
  });

  it('still renders every slot in compact mode', () => {
    renderLoadout({ compact: true });

    const rail = screen.getByTestId('material-loadout');
    expect(within(rail).getAllByRole('button')).toHaveLength(4);
  });
});
