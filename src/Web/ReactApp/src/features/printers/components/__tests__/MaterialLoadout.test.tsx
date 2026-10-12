
const permissions = vi.hoisted(() => ({ assign: true, operate: true }));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: (_resource: string, action: string) => action === 'write' ? permissions.assign : permissions.operate }),
}));
const inventory = vi.hoisted(() => ({ data: undefined as import('@/types/api').SpoolmanSpool[] | undefined, isError: false, refetch: vi.fn() }));
vi.mock('@/features/printers/hooks/usePrinterSpoolInventory', () => ({ usePrinterSpoolInventory: () => inventory }));
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { MaterialLoadout } from '@/features/printers/components/MaterialLoadout';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';
import type { MmuProtocolType } from '@/features/printers/constants/mmuProtocol';
import type { MmuGate, MmuStatus, ToolheadDto } from '@/types/api';
import { MmuGateStatus } from '@/types/api';

const setSpool = vi.fn();
const clearSpool = vi.fn();
const clearActiveSpool = vi.fn();
const setActiveSpool = vi.fn();
const coverage = vi.fn();
const printerDetails = vi.fn();
const pending = vi.hoisted(() => ({ set: false, clear: false, clearActive: false }));

vi.mock('@/common/hooks/useApi', () => ({
  useSetToolheadSpool: () => ({ mutateAsync: setSpool, isPending: pending.set }),
  useClearToolheadSpool: () => ({ mutateAsync: clearSpool, isPending: pending.clear }),
  useSetActiveSpool: () => ({ mutateAsync: setActiveSpool, isPending: false }),
  useClearActiveSpool: () => ({ mutateAsync: clearActiveSpool, isPending: pending.clearActive }),
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

function mmu(
  gates: MmuGate[],
  mmuType: MmuProtocolType = MmuProtocol.Qidibox,
): MmuStatus {
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
    permissions.assign = permissions.operate = true;
    inventory.data = undefined;
    inventory.isError = false;
    setSpool.mockReset().mockResolvedValue('rev-1');
    clearSpool.mockReset().mockResolvedValue('rev-1');
    clearActiveSpool.mockReset().mockResolvedValue('rev-1');
    setActiveSpool.mockReset().mockResolvedValue('rev-1');
    coverage.mockReset().mockReturnValue(undefined);
    printerDetails.mockReset().mockReturnValue({ data: undefined });
    pending.set = false;
    pending.clear = false;
    pending.clearActive = false;
  });

  it('renders one slot per physical filament position', () => {
    renderLoadout();

    expect(screen.getByTestId('material-loadout').querySelectorAll('[data-testid^="loadout-slot-"]')).toHaveLength(4);
    expect(screen.getByTestId('loadout-slot-3')).toBeInTheDocument();
  });

  it('renders the AD5X fourth lane from catalog identity when live MMU telemetry is absent', () => {
    const ad5xToolheads = persistedQidiBox.slice(0, 4);
    const { rerender } = renderLoadout({
      mmuStatus: undefined,
      toolheads: ad5xToolheads,
      modelName: 'Flashforge AD5X',
    });

    expect(screen.getByTestId('material-loadout').querySelectorAll('[data-testid^="loadout-slot-"]'))
      .toHaveLength(4);
    expect(screen.getByText('G4')).toBeInTheDocument();

    rerender(
      <MaterialLoadout
        printerId="printer-1"
        mmuStatus={undefined}
        toolheads={ad5xToolheads}
        reviewedRowVersion="rev-1"
      />,
    );
    expect(screen.getByTestId('material-loadout').querySelectorAll('[data-testid^="loadout-slot-"]'))
      .toHaveLength(3);
    expect(screen.queryByText('G4')).not.toBeInTheDocument();
  });

  it('shows a single detail panel instead of one card per slot', () => {
    renderLoadout();

    expect(screen.queryByTestId('slot-panel')).not.toBeInTheDocument();

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    expect(screen.getAllByTestId('slot-panel')).toHaveLength(1);

    fireEvent.click(screen.getByTestId('loadout-slot-2'));
    expect(screen.getAllByTestId('slot-panel')).toHaveLength(1);
  });

  it('collapses the detail panel when the selected slot is clicked again', () => {
    renderLoadout();

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    fireEvent.click(screen.getByTestId('loadout-slot-1'));

    expect(screen.queryByTestId('slot-panel')).not.toBeInTheDocument();
  });

  it('assigns a spool to the persisted index of the slot the user clicked', async () => {
    // Live gate 2 is persisted as toolhead 3; sending the live index would have
    // written the spool to the neighbouring gate.
    renderLoadout();

    fireEvent.click(screen.getByTestId('loadout-slot-2'));
    fireEvent.click(screen.getByTestId('slot-action-assign'));
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
    fireEvent.click(screen.getByTestId('slot-action-change'));
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
    const change = screen.getByTestId('slot-action-change');
    expect(change).not.toBeDisabled();
    expect(change).toHaveAttribute('aria-disabled', 'true');

    const clear = screen.getByTestId('slot-action-unassign');
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

    const change = screen.getByTestId('slot-action-change');
    const changeDescId = change.getAttribute('aria-describedby');
    expect(changeDescId).toBeTruthy();
    expect(document.getElementById(changeDescId!)).toHaveTextContent(
      'Disabled on the device — cannot take a spool',
    );

    const clear = screen.getByTestId('slot-action-unassign');
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
    fireEvent.click(screen.getByTestId('slot-action-assign'));
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
    fireEvent.click(screen.getByTestId('slot-action-unassign'));

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
    const assign = screen.getByTestId('slot-action-assign');

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
    expect(screen.getByTestId('slot-action-assign')).toHaveAttribute('aria-disabled', 'true');

    printerDetails.mockReturnValue({ data: { rowVersion: 'detail-rev-1' } });
    rerender(
      <MaterialLoadout
        printerId="printer-1"
        mmuStatus={mmu([gate(0), gate(1), gate(2), gate(3)])}
        toolheads={persistedQidiBox}
      />,
    );

    const assign = screen.getByTestId('slot-action-assign');
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
    fireEvent.click(screen.getByTestId('slot-action-assign'));
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
    await waitFor(() => expect(screen.getByTestId('slot-action-assign')).toBeEnabled());

    printerDetails.mockReturnValue({ data: undefined });
    rerender(
      <MaterialLoadout
        printerId="printer-1"
        mmuStatus={mmu([gate(0), gate(1), gate(2), gate(3)])}
        toolheads={persistedQidiBox}
      />,
    );

    expect(screen.getByTestId('slot-action-assign')).toBeEnabled();
    fireEvent.click(screen.getByTestId('slot-action-assign'));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() =>
      expect(setSpool).toHaveBeenCalledWith(
        expect.objectContaining({ reviewedRowVersion: 'detail-rev-1' }),
      ),
    );
  });

  it('lets an operator dismiss a selected slot with Escape without opening a picker', () => {
    renderLoadout();

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    fireEvent.keyDown(screen.getByTestId('slot-panel'), { key: 'Escape' });

    expect(screen.queryByTestId('slot-panel')).not.toBeInTheDocument();
  });

  it.each(['covers', 'unknown', 'runout'])(
    'hides idle %s badges and demand figures while keeping material slots', (status) => {
      coverage.mockReturnValue({
        printerId: 'printer-1', activeJobId: null, assignedQueuedJobCount: 0, status,
        toolheads: [{ toolheadIndex: 2, status, remainingGrams: 100, totalDemandGrams: 400 }],
      });
      renderLoadout();
      expect(screen.queryByRole('status')).not.toBeInTheDocument();
      expect(screen.getByTestId('loadout-slot-2')).toHaveAttribute('data-status', 'unknown');
      fireEvent.click(screen.getByTestId('loadout-slot-2'));
      expect(screen.queryByRole('status')).not.toBeInTheDocument();
      expect(screen.queryByText('400g needed')).not.toBeInTheDocument();
      expect(screen.getByTestId('material-loadout')).toBeInTheDocument();
    },
  );

  it('surfaces queued-job coverage only when it predicts a runout', () => {
    coverage.mockReturnValue({
      printerId: 'printer-1', activeJobId: null, assignedQueuedJobCount: 1,
      status: 'covers', toolheads: [],
    });
    const { rerender } = renderLoadout();
    // Option 4 keeps the resting state to one line: an all-clear adds nothing.
    expect(screen.queryByText('Filament OK')).not.toBeInTheDocument();

    coverage.mockReturnValue({
      printerId: 'printer-1', activeJobId: null, assignedQueuedJobCount: 1,
      status: 'runout', toolheads: [],
    });
    rerender(
      <MaterialLoadout
        printerId="printer-1"
        mmuStatus={mmu([gate(0), gate(1), gate(2), gate(3)])}
        toolheads={persistedQidiBox}
        reviewedRowVersion="rev-1"
      />,
    );
    expect(screen.getByText('Runout risk')).toBeInTheDocument();
  });

  it('joins coverage on the g-code index so figures land on the right slot', () => {
    coverage.mockReturnValue({
      activeJobId: 'job-1',
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
    const drawer = screen.getByTestId('slot-panel');
    expect(within(drawer).getByText('Needs 400 g')).toBeInTheDocument();
    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    expect(within(screen.getByTestId('slot-panel')).queryByText(/Needs/)).not.toBeInTheDocument();
  });

  // Regression test for issue #1684: this surface (reached from the
  // expanded DetailedPrinterCard and the printer details sidebar) rendered
  // the same false "Filament OK"/"Runout risk" success-or-warning claim as
  // the compact printer-list card, driven by the same stale last-known
  // coverage snapshot, whenever the printer was offline. `isOnline={false}`
  // must downgrade the header badge, the runout chip, and every per-slot
  // ring/badge to "unknown" regardless of what the backend last reported.
  it('downgrades every coverage status to unknown when the printer is offline (#1684)', () => {
    coverage.mockReturnValue({
      activeJobId: 'job-1',
      printerId: 'printer-1',
      printerName: 'Qidi Plus 4',
      status: 'covers',
      earliestPredictedRunoutAt: null,
      toolheads: [
        { toolheadIndex: 2, status: 'covers', remainingGrams: 900, totalDemandGrams: 400 },
      ],
    });

    renderLoadout({ isOnline: false });

    // Never claim "Filament OK" for a printer that cannot be verified.
    expect(screen.queryByText('Filament OK')).not.toBeInTheDocument();
    expect(screen.queryByText('Runout risk')).not.toBeInTheDocument();
    // Per-slot status must also be downgraded, not just the header rollup.
    expect(screen.getByTestId('loadout-slot-2')).toHaveAttribute('data-status', 'unknown');
  });

  it('suppresses the runout chip and warning ring when offline even for a last-known runout status (#1684)', () => {
    coverage.mockReturnValue({
      activeJobId: 'job-1',
      printerId: 'printer-1',
      printerName: 'Qidi Plus 4',
      status: 'runout',
      earliestPredictedRunoutAt: new Date(Date.now() + 20 * 60_000).toISOString(),
      toolheads: [
        { toolheadIndex: 2, status: 'runout', remainingGrams: 100, totalDemandGrams: 400 },
      ],
    });

    renderLoadout({ isOnline: false });

    expect(screen.queryByText('Runout risk')).not.toBeInTheDocument();
    expect(screen.queryByText(/Runs out in/i)).not.toBeInTheDocument();
    expect(screen.getByTestId('loadout-slot-2')).toHaveAttribute('data-status', 'unknown');
  });

  it('keeps existing (online) runout behavior when isOnline is not provided (back-compat)', () => {
    coverage.mockReturnValue({
      activeJobId: 'job-1',
      printerId: 'printer-1',
      printerName: 'Qidi Plus 4',
      status: 'runout',
      toolheads: [{ toolheadIndex: 2, status: 'runout', remainingGrams: 100, totalDemandGrams: 400 }],
    });

    renderLoadout();

    expect(screen.getByText('Runout risk')).toBeInTheDocument();
    expect(screen.getByTestId('loadout-slot-2')).toHaveAttribute('data-status', 'runout');
  });

  it('calls a toolchanger slot a tool, never an AMS gate', () => {
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1)], MmuProtocol.SnapmakerU1),
      toolheads: [persistedQidiBox[0]],
    });

    expect(screen.getByTestId('filament-summary')).toHaveTextContent('2 tools');
    expect(screen.queryByText(/AMS|slots/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    const drawer = screen.getByRole('region', { name: 'T1' });
    expect(within(drawer).getByTestId('slot-action-assign')).toHaveAccessibleName('Assign spool to T1');
    expect(within(drawer).queryByText(/Gate|Slot/)).not.toBeInTheDocument();
  });

  it('sends the unshifted index on a toolchanger', async () => {
    renderLoadout({
      mmuStatus: mmu([gate(0), gate(1)], MmuProtocol.SnapmakerU1),
      toolheads: [persistedQidiBox[0]],
    });

    fireEvent.click(screen.getByTestId('loadout-slot-1'));
    fireEvent.click(screen.getByTestId('slot-action-assign'));
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
    const assign = screen.getByTestId('slot-action-assign');

    // explainedDisabled: not natively disabled, but aria-disabled and inert.
    expect(assign).not.toBeDisabled();
    expect(assign).toHaveAttribute('aria-disabled', 'true');
    expect(assign).toHaveAttribute('tabindex', '0');
    // Toolheads not fetched yet: the hint says "loading", not "mismatch".
    expect(screen.getByText(/Loading saved gate layout/)).toBeInTheDocument();
    expect(screen.queryByText(/does not match the attached hardware/)).not.toBeInTheDocument();

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
    const assign = screen.getByTestId('slot-action-assign');

    expect(assign).not.toBeDisabled();
    expect(assign).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByText(/Saved gate layout does not match the attached hardware/)).toBeInTheDocument();
    fireEvent.click(assign);
    expect(setSpool).not.toHaveBeenCalled();
  });

  it('leaves Assign fully enabled when nothing blocks it', () => {
    // When canMutate=true, slot not disabled, and not busy — button is fully enabled.
    renderLoadout();

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    const assign = screen.getByTestId('slot-action-assign');

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
    const assign = screen.getByTestId('slot-action-assign');

    expect(assign).toBeDisabled();
    expect(assign).not.toHaveAttribute('aria-disabled', 'true');
    expect(assign).not.toHaveAttribute('tabindex', '0');
  });

  it('associates blocked buttons with an accessible description via aria-describedby', () => {
    // When canMutate is false, the reason text rendered in the drawer must be
    // programmatically associated with the button so screen readers announce it.
    renderLoadout({ reviewedRowVersion: undefined });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    const assign = screen.getByTestId('slot-action-assign');

    const descId = assign.getAttribute('aria-describedby');
    expect(descId).toBeTruthy();
    const desc = document.getElementById(descId!);
    expect(desc).toBeInTheDocument();
    expect(desc).toHaveTextContent(/revision unavailable/i);
    expect(assign).toHaveAccessibleDescription(/revision unavailable/i);
  });

  it('keeps external hotend testid distinct from the first MMU gate at G-code 0 (blocker 5)', () => {
    // Persisted-only fallback with an external hotend at physical index 0 and
    // gates starting at persisted index 1 (gcode 0). The two rows must remain
    // distinguishable via testids and data-source, and only the gate row must
    // receive the g-code-indexed coverage entry.
    coverage.mockReturnValue({
      activeJobId: 'job-1',
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
      activeJobId: 'job-1',
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
    const drawer = screen.getByTestId('slot-panel');
    expect(within(drawer).getByText('Disabled on device')).toBeInTheDocument();

    const assign = screen.getByTestId('slot-action-assign');
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

    fireEvent.click(screen.getByTestId('slot-action-assign'));
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

    fireEvent.click(screen.getByTestId('slot-action-assign'));
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
    fireEvent.click(screen.getByTestId('slot-action-change'));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() =>
      expect(setSpool).toHaveBeenCalledWith(
        expect.objectContaining({ reviewedRowVersion: 'rev-1' }),
      ),
    );

    fireEvent.click(screen.getByTestId('slot-action-unassign'));

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
    fireEvent.click(screen.getByTestId('slot-action-assign'));
    fireEvent.click(await screen.findByTestId('spool-picker'));

    await waitFor(() => expect(setSpool).toHaveBeenCalled());
    // The picker stays mounted so the user can retry rather than losing context.
    expect(screen.getByTestId('spool-picker')).toBeInTheDocument();
    expect(screen.getByTestId('slot-panel')).toBeInTheDocument();
  });

  // ── Behaviour inherited from the retired AmsSlotVisualization suite ──

  it('marks every unassigned slot rather than implying physical loading', () => {
    renderLoadout({
      mmuStatus: mmu([
        gate(0, { material: 'PLA' }),
        gate(1, { material: undefined, spoolId: 0 }),
        gate(2, { material: undefined, spoolId: 0 }),
        gate(3, { material: undefined, spoolId: 0 }),
      ]),
    });

    for (const index of [0, 1, 2, 3]) {
      expect(screen.getByTestId(`loadout-slot-${index}`)).toHaveAttribute('data-state', 'none');
    }
    expect(screen.queryByText(/assigned/)).not.toBeInTheDocument();
  });

  it('describes an empty slot as empty rather than showing a stale material', () => {
    renderLoadout({
      mmuStatus: mmu([gate(0, { material: undefined, spoolId: 0 }), gate(1), gate(2), gate(3)]),
    });

    expect(screen.getByTestId('loadout-slot-0')).toHaveAccessibleName('G1: Assign spool');
    expect(screen.getByTestId('loadout-slot-0')).toHaveAttribute('data-state', 'none');

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    const panel = screen.getByTestId('slot-panel');
    expect(within(panel).getByTestId('slot-action-assign')).toBeInTheDocument();
    expect(within(panel).queryByText('No spool assigned')).not.toBeInTheDocument();
  });

  it('exposes the device-reported slot name without crowding the rail', async () => {
    renderLoadout({
      mmuStatus: mmu([gate(0, { name: 'Left feeder' }), gate(1), gate(2), gate(3)]),
    });

    // The rail keeps the terse label; the full device name appears in the
    // slot panel so a 4-slot strip stays scannable.
    const slot = screen.getByTestId('loadout-slot-0');
    expect(within(slot).getByText('G1')).toBeInTheDocument();
    expect(screen.queryByText('Left feeder')).not.toBeInTheDocument();

    fireEvent.click(slot);
    expect(await screen.findByTestId('slot-device-name')).toHaveTextContent('Left feeder');
  });

  it('does not repeat the device name when it adds nothing', () => {
    renderLoadout({ mmuStatus: mmu([gate(0, { name: 'G1' }), gate(1), gate(2), gate(3)]) });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    expect(screen.queryByTestId('slot-device-name')).not.toBeInTheDocument();
  });

  it('does not restate a generic positional name such as "Gate 1"', () => {
    renderLoadout({ mmuStatus: mmu([gate(0, { name: 'Gate 1' }), gate(1), gate(2), gate(3)]) });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    expect(screen.queryByTestId('slot-device-name')).not.toBeInTheDocument();
  });

  it('still renders every slot in compact mode', () => {
    renderLoadout({ compact: true });

    expect(screen.getByTestId('material-loadout').querySelectorAll('[data-testid^="loadout-slot-"]')).toHaveLength(4);
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
    expect(slot1).not.toHaveAttribute('aria-current');
    // A feed with nothing loaded is never presented as the in-use spool.
    expect(screen.queryByTestId('filament-summary-line')).not.toBeInTheDocument();
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
    expect(slot1.getAttribute('aria-label')).toContain('in use');
  });
});


describe('MaterialLoadout independent material facts', () => {
  beforeEach(() => {
    permissions.assign = permissions.operate = true;
    inventory.data = undefined;
    inventory.isError = false;
    coverage.mockReturnValue(undefined);
    pending.set = pending.clear = pending.clearActive = false;
    setSpool.mockReset().mockResolvedValue('rev-2');
    clearSpool.mockReset().mockResolvedValue('rev-2');
  });

  it('distinguishes unassigned, confirmed depleted, unknown and unloaded without rings', () => {
    inventory.data = [{ id: 7, name: 'PLA', material: 'PLA', remainingWeightG: 0, inUse: false }];
    renderLoadout({ mmuStatus: mmu([gate(0), gate(1, { spoolId: 7 }), gate(2, { spoolId: 8 }), gate(3, { spoolId: 9, status: MmuGateStatus.Empty })]) });
    const slot = (i: number) => screen.getByTestId(`loadout-slot-${i}`);
    // No spool assigned: spool outline with a "+" badge.
    expect(slot(0)).toHaveAttribute('data-state', 'none');
    expect(within(slot(0)).getByTestId('slot-badge-assign')).toBeInTheDocument();
    // Confirmed depleted by Spoolman: a distinct "!" badge, never the "+".
    expect(slot(1)).toHaveAttribute('data-state', 'depleted');
    expect(within(slot(1)).getByTestId('slot-badge-depleted')).toBeInTheDocument();
    expect(within(slot(1)).queryByTestId('slot-badge-assign')).not.toBeInTheDocument();
    // The gate still reports live filament, so only the inventory is at 0 g.
    expect(slot(1)).toHaveAccessibleName(/0 g remaining/);
    expect(slot(1)).not.toHaveAccessibleName(/empty/);
    // Unknown remaining is still an assigned spool and never shows a gram figure.
    expect(slot(2)).toHaveAttribute('data-state', 'assigned');
    expect(slot(2)).not.toHaveAccessibleName(/\d+ g/);
    // A device-empty gate keeps its binding; feed state does not fake depletion.
    expect(slot(3)).toHaveAttribute('data-state', 'assigned');
    fireEvent.click(slot(3));
    expect(within(screen.getByTestId('slot-panel')).getByText('Spool #9')).toBeInTheDocument();
    expect(screen.getByTestId('material-loadout').querySelector('[stroke-dasharray]')).toBeNull();
  });

  it.each([1, 2])('assigns through the same picker for %s physical toolheads', async (count) => {
    renderLoadout({ mmuStatus: undefined, toolheads: Array.from({ length: count }, (_, index) => ({ id: `th-${index}`, index, name: `Tool ${index}`, toolheadType: 'Physical' }) as ToolheadDto) });
    const slot = screen.getByTestId(`loadout-slot-${count - 1}`);
    expect(slot).toHaveAttribute('data-state', 'none');
    expect(screen.queryByText(/No spool assigned|Loading state unknown/)).not.toBeInTheDocument();
    fireEvent.click(slot);
    fireEvent.click(screen.getByTestId('slot-action-assign'));
    fireEvent.click(screen.getByTestId('spool-picker'));
    await waitFor(() => expect(setSpool).toHaveBeenCalledWith({ printerId: 'printer-1', toolheadIndex: count - 1, spoolId: 99, reviewedRowVersion: 'rev-1' }));
  });

  it('unassigns a synthesized legacy T0 through active-spool and uses its returned revision for the next bind', async () => {
    clearActiveSpool.mockReset().mockResolvedValue('rev-after-clear');
    setActiveSpool.mockReset().mockResolvedValue('rev-after-assign');
    renderLoadout({
      mmuStatus: undefined,
      toolheads: [],
      currentSpoolId: 42,
    });

    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    fireEvent.click(screen.getByTestId('slot-action-unassign'));

    await waitFor(() => expect(clearActiveSpool).toHaveBeenCalledWith({
      printerId: 'printer-1',
      reviewedRowVersion: 'rev-1',
    }));
    expect(clearSpool).not.toHaveBeenCalled();

    // The post-clear revision must be the anchor for the next write even if
    // the printer-detail query has not refreshed yet.
    fireEvent.click(screen.getByTestId('slot-action-change'));
    fireEvent.click(await screen.findByTestId('spool-picker'));
    // Props still describe a printer-scalar binding, so the rebind goes through
    // the same scalar endpoint — never a toolhead write.
    await waitFor(() => expect(setActiveSpool).toHaveBeenCalledWith({
      printerId: 'printer-1',
      spoolId: 99,
      reviewedRowVersion: 'rev-after-clear',
    }));
    expect(setSpool).not.toHaveBeenCalled();
  });

  it('keeps an empty single-feed printer assignable after saved topology loads', async () => {
    renderLoadout({ mmuStatus: undefined, toolheads: [] });
    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    fireEvent.click(screen.getByTestId('slot-action-assign'));
    fireEvent.click(screen.getByTestId('spool-picker'));
    await waitFor(() => expect(setSpool).toHaveBeenCalledWith(expect.objectContaining({ toolheadIndex: 0 })));
  });

  it('blocks inventory writes without permission while slots remain inspectable', () => {
    permissions.assign = false;
    renderLoadout();
    fireEvent.click(screen.getByTestId('loadout-slot-0'));
    fireEvent.click(screen.getByTestId('slot-action-assign'));
    expect(screen.getByTestId('slot-action-assign')).toHaveAttribute('aria-disabled', 'true');
    expect(screen.queryByTestId('spool-picker')).not.toBeInTheDocument();
    expect(setSpool).not.toHaveBeenCalled();
  });

  it('reports inventory errors as unknown, not depleted', () => {
    inventory.data = [{ id: 7, name: 'PLA', material: 'PLA', remainingWeightG: 0, inUse: false }];
    inventory.isError = true;
    renderLoadout({ mmuStatus: mmu([gate(0, { spoolId: 7 })]), toolheads: persistedQidiBox.slice(0, 2) });
    expect(screen.getByTestId('loadout-slot-0')).toHaveAttribute('data-state', 'assigned');
    expect(screen.queryByTestId('slot-badge-depleted')).not.toBeInTheDocument();
    expect(screen.queryByText(/Spoolman unavailable/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument();
  });
});


it('prevents rapid repeated confirmation from dispatching concurrent spool writes', async () => {
  permissions.assign = permissions.operate = true;
  inventory.data = undefined;
  inventory.isError = false;
  let finish: (revision: string) => void = () => {};
  setSpool.mockReset().mockImplementation(() => new Promise<string>((resolve) => { finish = resolve; }));
  renderLoadout();
  fireEvent.click(screen.getByTestId('loadout-slot-0'));
  fireEvent.click(screen.getByTestId('slot-action-assign'));
  fireEvent.click(screen.getByTestId('spool-picker'));
  fireEvent.click(screen.getByTestId('spool-picker'));
  expect(setSpool).toHaveBeenCalledTimes(1);
  finish('rev-2');
  await waitFor(() => expect(screen.queryByTestId('spool-picker')).not.toBeInTheDocument());
});
