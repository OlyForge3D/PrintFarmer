import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { MaterialLoadout } from '@/features/printers/components/MaterialLoadout';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';
import type { MmuGate, MmuStatus, ToolheadDto } from '@/types/api';
import { MmuGateStatus } from '@/types/api';

const setSpool = vi.fn();
const clearSpool = vi.fn();
const coverage = vi.fn();
const printerDetails = vi.fn();
const pending = vi.hoisted(() => ({ set: false, clear: false }));

vi.mock('@/common/hooks/useApi', () => ({
  useSetToolheadSpool: () => ({ mutateAsync: setSpool, isPending: pending.set }),
  useClearToolheadSpool: () => ({ mutateAsync: clearSpool, isPending: pending.clear }),
  usePrinterDetails: (...args: unknown[]) => printerDetails(...args),
}));

vi.mock('@/features/filament-coverage/hooks', () => ({
  usePrinterCoverageFromFleet: () => ({ data: coverage() }),
}));

vi.mock('@/features/printers/components/SpoolPickerModal', () => ({
  SpoolPickerModal: ({ onSelect }: { onSelect: (id: number) => void }) => (
    <>
      <button type="button" data-testid="spool-picker" onClick={() => onSelect(99)}>
        pick
      </button>
      {/* The real picker's Eject action reports spool id 0. */}
      <button type="button" data-testid="spool-picker-eject" onClick={() => onSelect(0)}>
        eject
      </button>
    </>
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
    // Real setToolheadSpool/clearToolheadSpool resolve to the new rowVersion
    // string, which lockedRevision re-anchors to on success.
    setSpool.mockReset().mockResolvedValue('rev-1');
    clearSpool.mockReset().mockResolvedValue('rev-1');
    coverage.mockReset().mockReturnValue(undefined);
    printerDetails.mockReset().mockReturnValue({ data: undefined });
    pending.set = false;
    pending.clear = false;
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

  it('routes the picker eject action to the clear endpoint, not a spool-0 bind', async () => {
    // SpoolPickerModal's Eject button reports spool id 0. Forwarding that to
    // setSpool would persist a bogus zero binding instead of releasing the slot.
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1, { spoolId: 42 }), gate(2), gate(3)]),
    });

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    fireEvent.click(screen.getByRole('button', { name: 'Change' }));
    fireEvent.click(await screen.findByTestId('spool-picker-eject'));

    await waitFor(() =>
      expect(clearSpool).toHaveBeenCalledWith({
        printerId: 'printer-1',
        toolheadIndex: 2,
        reviewedRowVersion: 'rev-1',
      }),
    );
    expect(setSpool).not.toHaveBeenCalled();
  });

  it('still allows clearing a stale binding from a disabled gate', async () => {
    // Assign is blocked on a disabled gate, but if the device disabled a gate
    // that still carries a binding, release has to stay reachable. The picker
    // is unreachable here, so Clear in the drawer is the only path.
    renderLoadout({
      mmuStatus: mmu([
        gate(0),
        gate(1, { spoolId: 42, status: MmuGateStatus.Disabled }),
        gate(2),
        gate(3),
      ]),
    });

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    const change = screen.getByRole('button', { name: 'Change' });
    expect(change).not.toBeDisabled();
    expect(change).toHaveAttribute('aria-disabled', 'true');

    const clear = screen.getByRole('button', { name: 'Clear' });
    expect(clear).toBeEnabled();
    fireEvent.click(clear);

    await waitFor(() =>
      expect(clearSpool).toHaveBeenCalledWith({
        printerId: 'printer-1',
        toolheadIndex: 2,
        reviewedRowVersion: 'rev-1',
      }),
    );
    expect(setSpool).not.toHaveBeenCalled();
  });

  it("names Clear's real blocker, not the disabled-gate reason, when a gate is both disabled and unrevisioned", () => {
    // Clear stays available on a disabled gate (see the test above); its only
    // real blocker here is the missing revision, not the device-disabled
    // state. A screen-reader user focusing Clear must hear that, not the
    // disabled-gate text that Assign/Change legitimately shows.
    renderLoadout({
      mmuStatus: mmu([
        gate(0),
        gate(1, { spoolId: 42, status: MmuGateStatus.Disabled }),
        gate(2),
        gate(3),
      ]),
      reviewedRowVersion: null,
    });

    fireEvent.click(screen.getByTestId('loadout-slot-1'));

    const change = screen.getByRole('button', { name: 'Change' });
    const changeDescId = change.getAttribute('aria-describedby');
    expect(changeDescId).toBeTruthy();
    expect(document.getElementById(changeDescId!)).toHaveTextContent(
      'Disabled on the device — cannot take a spool',
    );

    const clear = screen.getByRole('button', { name: 'Clear' });
    const clearDescId = clear.getAttribute('aria-describedby');
    expect(clearDescId).toBeTruthy();
    expect(clearDescId).not.toBe(changeDescId);
    expect(document.getElementById(clearDescId!)).toHaveTextContent(
      'Printer revision unavailable — refresh to assign spools',
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

  it('clears a spool from the persisted index of the slot the user clicked', async () => {
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1, { spoolId: 12 }), gate(2), gate(3)]),
    });

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));

    await waitFor(() =>
      expect(clearSpool).toHaveBeenCalledWith({
        printerId: 'printer-1',
        toolheadIndex: 2,
        reviewedRowVersion: 'rev-1',
      }),
    );
  });

  it('keeps assignment blocked while a selected slot detail revision is loading', () => {
    renderLoadout({ reviewedRowVersion: undefined });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    const assign = screen.getByRole('button', { name: 'Assign' });

    expect(assign).not.toBeDisabled();
    expect(assign).toHaveAttribute('aria-disabled', 'true');
    expect(assign).toHaveAttribute('tabindex', '0');
    expect(screen.getByText(/Printer revision unavailable/)).toBeInTheDocument();

    fireEvent.click(assign);
    expect(screen.queryByTestId('spool-picker')).not.toBeInTheDocument();
    expect(setSpool).not.toHaveBeenCalled();
  });

  it('enables assignment when the initial detail revision arrives after a slot is selected', async () => {
    const { rerender } = renderLoadout({ reviewedRowVersion: undefined });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    expect(screen.getByRole('button', { name: 'Assign' })).toHaveAttribute('aria-disabled', 'true');

    printerDetails.mockReturnValue({ data: { rowVersion: 'detail-rev-1' } });
    rerender(
      <MaterialLoadout
        printerId="printer-1"
        mmuStatus={mmu([gate(0), gate(1), gate(2), gate(3)])}
        toolheads={persistedQidiBox}
      />,
    );

    const assign = screen.getByRole('button', { name: 'Assign' });
    await waitFor(() => expect(assign).toBeEnabled());
    fireEvent.click(assign);
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() =>
      expect(setSpool).toHaveBeenCalledWith(
        expect.objectContaining({ reviewedRowVersion: 'detail-rev-1' }),
      ),
    );
  });

  it('uses the authoritative detail revision when the card revision is not yet available', async () => {
    printerDetails.mockReturnValue({ data: { rowVersion: 'detail-rev-1' } });
    renderLoadout({ reviewedRowVersion: undefined });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() =>
      expect(setSpool).toHaveBeenCalledWith({
        printerId: 'printer-1',
        toolheadIndex: 1,
        spoolId: 99,
        reviewedRowVersion: 'detail-rev-1',
      }),
    );
  });

  it('keeps the first delayed detail revision when later detail state is unavailable', async () => {
    const { rerender } = renderLoadout({ reviewedRowVersion: undefined });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));

    printerDetails.mockReturnValue({ data: { rowVersion: 'detail-rev-1' } });
    rerender(
      <MaterialLoadout
        printerId="printer-1"
        mmuStatus={mmu([gate(0), gate(1), gate(2), gate(3)])}
        toolheads={persistedQidiBox}
      />,
    );
    await waitFor(() => expect(screen.getByRole('button', { name: 'Assign' })).toBeEnabled());

    printerDetails.mockReturnValue({ data: undefined });
    rerender(
      <MaterialLoadout
        printerId="printer-1"
        mmuStatus={mmu([gate(0), gate(1), gate(2), gate(3)])}
        toolheads={persistedQidiBox}
      />,
    );

    expect(screen.getByRole('button', { name: 'Assign' })).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() =>
      expect(setSpool).toHaveBeenCalledWith(
        expect.objectContaining({ reviewedRowVersion: 'detail-rev-1' }),
      ),
    );
  });

  it('lets an operator cancel a selected slot without opening a picker', () => {
    renderLoadout();

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByTestId('loadout-drawer')).not.toBeInTheDocument();
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

  it('renders a single-slot rail for a printer with one physical toolhead', () => {
    renderLoadout({ mmuStatus: undefined, toolheads: [persistedQidiBox[0]] });

    const rail = screen.getByTestId('material-loadout');
    expect(within(rail).getAllByRole('button')).toHaveLength(1);
  });

  it('gates spool mutation on persisted topology for a live-MMU printer (blocker 2)', () => {
    // Live MMU status arrives via SignalR before the persisted topology fetch
    // resolves. Without topology, mapping live G1 to the persisted index is a
    // guess and could write G1 to physical hotend index 0. Assignment must be
    // blocked with an explanatory hint until topology arrives.
    renderLoadout({ toolheads: undefined });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    const assign = screen.getByRole('button', { name: 'Assign' });

    // explainedDisabled: not natively disabled, but aria-disabled and inert.
    expect(assign).not.toBeDisabled();
    expect(assign).toHaveAttribute('aria-disabled', 'true');
    expect(assign).toHaveAttribute('tabindex', '0');
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

    expect(assign).not.toBeDisabled();
    expect(assign).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByText(/Materials topology not yet loaded/)).toBeInTheDocument();
    fireEvent.click(assign);
    expect(setSpool).not.toHaveBeenCalled();
  });

  it('leaves Assign fully enabled when nothing blocks it', () => {
    // When canMutate=true, slot not disabled, and not busy — button is fully enabled.
    renderLoadout();

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    const assign = screen.getByRole('button', { name: 'Assign' });

    expect(assign).toBeEnabled();
    expect(assign).not.toHaveAttribute('aria-disabled');
    expect(assign).not.toHaveAttribute('tabindex', '0');
  });

  it('uses native disabled (not explainedDisabled) when only busy', () => {
    // When busy (isPending) but canMutate=true and slot not disabled, the button
    // must be natively disabled (out of tab order) — NOT explainedDisabled.
    // A transient in-flight state needs no explanation and should not linger focusable.
    pending.set = true;
    renderLoadout();

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    const assign = screen.getByRole('button', { name: 'Assign' });

    expect(assign).toBeDisabled();
    expect(assign).not.toHaveAttribute('aria-disabled', 'true');
    expect(assign).not.toHaveAttribute('tabindex', '0');
  });

  it('associates blocked buttons with an accessible description via aria-describedby', () => {
    // When canMutate is false, the reason text rendered in the drawer must be
    // programmatically associated with the button so screen readers announce it.
    renderLoadout({ reviewedRowVersion: undefined });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    const assign = screen.getByRole('button', { name: 'Assign' });

    expect(assign).toHaveAttribute('aria-describedby', 'loadout-action-desc-printer-1');
    const desc = document.getElementById('loadout-action-desc-printer-1');
    expect(desc).toBeInTheDocument();
    expect(desc).toHaveTextContent(/revision unavailable/i);
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
    expect(assign).not.toBeDisabled();
    expect(assign).toHaveAttribute('aria-disabled', 'true');
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

  it('re-anchors the revision from a successful mutation so the next action in the same drawer is not stale', async () => {
    // Assign then Clear in one open drawer without the printer round-tripping
    // a fresh `reviewedRowVersion` prop in between (the mutation's own
    // success response is the only source of the new revision here). If
    // lockedRevision were not re-anchored after Assign, Clear would still
    // send the pre-assign 'rev-1' and 412 against what Assign just wrote.
    setSpool.mockResolvedValue('rev-2');
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1, { spoolId: 42 }), gate(2), gate(3)]),
    });

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    fireEvent.click(screen.getByRole('button', { name: 'Change' }));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() =>
      expect(setSpool).toHaveBeenCalledWith(
        expect.objectContaining({ reviewedRowVersion: 'rev-1' }),
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));

    await waitFor(() =>
      expect(clearSpool).toHaveBeenCalledWith(
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

  it('renders a single-slot rail for a single-toolhead printer', () => {
    const singleToolhead: ToolheadDto[] = [
      { id: 'th-0', index: 0, name: 'Hotend', toolheadType: 'Physical', currentMaterial: 'PLA', currentFilamentColor: '#00ff00' } as ToolheadDto,
    ];
    renderLoadout({ mmuStatus: undefined, toolheads: singleToolhead });

    const rail = screen.getByTestId('material-loadout');
    expect(within(rail).getAllByRole('button')).toHaveLength(1);
    expect(screen.getByTestId('loadout-slot-0')).toBeInTheDocument();
  });

  it('marks the active slot for an MMU gate with filament loaded', () => {
    const status = {
      ...mmu([gate(0), gate(1), gate(2), gate(3)]),
      activeGate: 2,
      activeTool: 0,
      filamentState: 'Loaded',
    };
    renderLoadout({ mmuStatus: status });

    const slot2 = screen.getByTestId('loadout-slot-2');
    expect(slot2).toHaveAttribute('data-active', 'loaded');
    expect(slot2).toHaveAttribute('aria-current', 'true');
  });

  it('marks the active slot as selected (not loaded) when filamentState is Unloaded', () => {
    const status = {
      ...mmu([gate(0), gate(1), gate(2), gate(3)]),
      activeGate: 1,
      activeTool: 0,
      filamentState: 'Unloaded',
    };
    renderLoadout({ mmuStatus: status });

    const slot1 = screen.getByTestId('loadout-slot-1');
    expect(slot1).toHaveAttribute('data-active', 'selected');
  });

  it('marks the active slot for a toolchanger using activeTool', () => {
    const status = {
      ...mmu([gate(0), gate(1)], MmuProtocol.SnapmakerU1),
      activeGate: 1,
      activeTool: 1,
      filamentState: 'Loaded',
    };
    renderLoadout({ mmuStatus: status, toolheads: undefined });

    const slot1 = screen.getByTestId('loadout-slot-1');
    expect(slot1).toHaveAttribute('data-active', 'loaded');
  });

  it('does not mark any slot active when sentinel is -1', () => {
    const status = {
      ...mmu([gate(0), gate(1), gate(2), gate(3)]),
      activeGate: -1,
      activeTool: -1,
    };
    renderLoadout({ mmuStatus: status });

    const buttons = screen.getAllByRole('button');
    buttons.forEach((btn) => {
      expect(btn).not.toHaveAttribute('data-active');
    });
  });

  it('does not mark any slot active when sentinel is -2', () => {
    const status = {
      ...mmu([gate(0), gate(1), gate(2), gate(3)]),
      activeGate: -2,
      activeTool: -2,
    };
    renderLoadout({ mmuStatus: status });

    const buttons = screen.getAllByRole('button');
    buttons.forEach((btn) => {
      expect(btn).not.toHaveAttribute('data-active');
    });
  });

  it('never marks an external slot as active even when activeGate matches its apiIndex', () => {
    // Use the toolheads-only path (no live gates) which creates external slots
    // from physical toolheads alongside MmuGate entries.
    const physicalWithSpool: ToolheadDto[] = [
      { id: 'th-0', index: 0, name: 'Hotend', toolheadType: 'Physical', currentSpoolId: 5, currentMaterial: 'ABS' } as ToolheadDto,
      { id: 'th-1', index: 1, name: 'Gate 1', toolheadType: 'MmuGate' } as ToolheadDto,
      { id: 'th-2', index: 2, name: 'Gate 2', toolheadType: 'MmuGate' } as ToolheadDto,
    ];
    // mmuStatus has activeGate=0 but no live gates — forces the toolheads-only path.
    const status = {
      enabled: true,
      mmuType: MmuProtocol.Qidibox,
      numGates: 0,
      gates: [],
      activeGate: 0,
      activeTool: 0,
      filamentState: 'Loaded',
    } as unknown as MmuStatus;
    renderLoadout({ mmuStatus: status, toolheads: physicalWithSpool });

    const externalSlot = screen.getByTestId('loadout-slot-external-0');
    expect(externalSlot).not.toHaveAttribute('data-active');
  });

  it('includes active/loaded status in aria-label', () => {
    const status = {
      ...mmu([gate(0), gate(1), gate(2), gate(3)]),
      activeGate: 1,
      activeTool: 0,
      filamentState: 'Loaded',
    };
    renderLoadout({ mmuStatus: status });

    const slot1 = screen.getByTestId('loadout-slot-1');
    expect(slot1.getAttribute('aria-label')).toContain('active and loaded');
  });
});
