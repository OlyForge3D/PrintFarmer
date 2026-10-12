
const permissions = vi.hoisted(() => ({ assign: true, operate: true }));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: (_resource: string, action: string) => action === 'write' ? permissions.assign : permissions.operate }),
}));
const inventory = vi.hoisted(() => ({ data: undefined as import('@/types/api').SpoolmanSpool[] | undefined, isError: false, refetch: vi.fn() }));
vi.mock('@/features/printers/hooks/usePrinterSpoolInventory', () => ({ usePrinterSpoolInventory: () => inventory }));
import type { ReactElement } from 'react';
import { fireEvent, render as rtlRender, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { MmuControlBox } from '../MmuControlBox';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';
import { MmuGateStatus, type MmuGate, type MmuStatus, type ToolheadDto } from '@/types/api';

const setSpool = vi.fn();
const clearSpool = vi.fn();
const clearActiveSpool = vi.fn();
const coverage = vi.fn();
let queryClient: QueryClient;

vi.mock('@/common/hooks/useApi', () => ({
  useSetToolheadSpool: () => ({ mutateAsync: setSpool, isPending: false }),
  useClearToolheadSpool: () => ({ mutateAsync: clearSpool, isPending: false }),
  useSetActiveSpool: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useClearActiveSpool: () => ({ mutateAsync: clearActiveSpool, isPending: false }),
  usePrinterDetails: () => ({ data: undefined }),
  queryKeys: { printerDetails: (id: string) => ['printers', id, 'details'] },
}));

vi.mock('@/features/filament-coverage/hooks', () => ({
  usePrinterCoverageFromFleet: () => ({ data: coverage() }),
}));

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

function render(ui: ReactElement) {
  const result = rtlRender(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
  return {
    ...result,
    rerender: (next: ReactElement) =>
      result.rerender(<QueryClientProvider client={queryClient}>{next}</QueryClientProvider>),
  };
}

vi.mock('@/services/api', () => ({
  apiClient: { mmuGateAction: vi.fn(), mmuEject: vi.fn(), mmuChangeTool: vi.fn(), mmuHome: vi.fn(), mmuRecover: vi.fn() },
}));

vi.mock('@/features/printers/components/SpoolPickerModal', () => ({
  SpoolPickerModal: ({ onSelect }: { onSelect: (id: number) => void }) => (
    <>
      <button type="button" data-testid="spool-picker" onClick={() => onSelect(99)}>pick</button>
      <button type="button" data-testid="spool-picker-eject" onClick={() => onSelect(0)}>eject</button>
    </>
  ),
}));

function gate(index: number, overrides: Partial<MmuGate> = {}): MmuGate {
  return {
    index,
    status: MmuGateStatus.Available,
    material: 'PLA',
    color: '#ff0000',
    filamentName: 'Test PLA',
    spoolId: 0,
    ...overrides,
  };
}

function status(gates: MmuGate[], overrides: Partial<MmuStatus> = {}): MmuStatus {
  return {
    enabled: true,
    isHomed: true,
    activeTool: 0,
    activeGate: 0,
    filamentState: 'Loaded',
    action: 'Idle',
    numGates: gates.length,
    hasBypass: false,
    endlessSpool: false,
    clogDetection: false,
    gates,
    ...overrides,
  };
}

function toolhead(index: number, toolheadType: string, overrides: Partial<ToolheadDto> = {}): ToolheadDto {
  return { id: `th-${index}`, index, name: `Toolhead ${index}`, toolheadType, ...overrides } as ToolheadDto;
}

// The live qp4-1 shape: four QidiBox gates, only three persisted gate rows.
const qidiGates = [gate(0), gate(1), gate(2), gate(3)];
const threePersistedGates = [
  toolhead(0, 'Physical'),
  toolhead(1, 'MmuGate'),
  toolhead(2, 'MmuGate'),
  toolhead(3, 'MmuGate'),
];

describe('MmuControlBox', () => {
  beforeEach(() => {
    queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    permissions.assign = permissions.operate = true;
    inventory.data = undefined;
    inventory.isError = false;
    setSpool.mockReset().mockResolvedValue('rev-2');
    clearSpool.mockReset().mockResolvedValue('rev-2');
    coverage.mockReset().mockReturnValue(undefined);
    vi.mocked(toast.error).mockReset();
    vi.mocked(apiClient.mmuGateAction).mockReset().mockResolvedValue({ success: true });
  });

  const twoGateToolheads = [toolhead(0, 'Physical'), toolhead(1, 'MmuGate'), toolhead(2, 'MmuGate')];
  // Option 4: the rail is disclosed from the resting line and each slot opens
  // one shared panel of icon buttons whose accessible names carry the slot id.
  const openSlot = (testId: string) => {
    fireEvent.click(screen.getByTestId(testId));
    return screen.getByTestId('slot-panel');
  };
  const action = (kind: string) =>
    within(screen.getByTestId('slot-panel')).getByTestId(`slot-action-${kind}`);
  const queryAction = (kind: string) =>
    within(screen.getByTestId('slot-panel')).queryByTestId(`slot-action-${kind}`);
  const isBlocked = (el: HTMLElement) =>
    el.getAttribute('aria-disabled') === 'true' || (el as HTMLButtonElement).disabled;
  const railSlots = () =>
    Array.from(screen.getByTestId('mmu-slots').querySelectorAll('[data-slot-key]'))
      .filter((el) => el.getAttribute('data-testid') !== null && el.closest('[role="group"]') !== null);

  it('rests on one line for the loaded QidiBox gate and discloses only the four gates when Rack is absent', () => {
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, activeGate: 1 })}
        isOnline
        toolheads={threePersistedGates}
        reviewedRowVersion="rev-1"
      />,
    );

    const summary = screen.getByTestId('filament-summary');
    expect(within(summary).getByTestId('filament-summary-line')).toHaveTextContent(/1B·PLA/);
    expect(summary).toHaveAttribute('aria-expanded', 'false');
    const rail = document.getElementById(summary.getAttribute('aria-controls')!);
    expect(rail).not.toBeVisible();
    fireEvent.click(summary);
    expect(summary).toHaveAttribute('aria-expanded', 'true');
    expect(rail).toBeVisible();

    expect(screen.queryByTestId('mmu-rack')).not.toBeInTheDocument();
    expect(railSlots().map((el) => el.getAttribute('data-testid')))
      .toEqual(['mmu-gate-0', 'mmu-gate-1', 'mmu-gate-2', 'mmu-gate-3']);
    expect(screen.getByTestId('mmu-gate-1')).toHaveAttribute('aria-current', 'true');
    expect(screen.getByTestId('mmu-gate-1')).toHaveAttribute('data-active', 'loaded');
    expect(document.querySelectorAll('[aria-current="true"]')).toHaveLength(1);
  });

  it.each([MmuProtocol.HappyHare, MmuProtocol.Afc])('marks the actual feed without a duplicate Rack for %s', (mmuType) => {
    render(<MmuControlBox printerId="printer-1" mmuStatus={status([gate(0, { material: 'PETG' }), gate(1)], { mmuType })} isOnline />);
    expect(screen.queryByTestId('mmu-rack')).not.toBeInTheDocument();
    expect(screen.getByTestId('filament-summary-line')).toHaveTextContent(/1A·PETG/);
    const active = screen.getByTestId('mmu-gate-0');
    expect(active).toHaveAttribute('aria-current', 'true');
    expect(active).toHaveAccessibleName(/^1A: PETG.*in use/);
    expect(active.querySelector('svg')).toHaveAttribute('viewBox', '0 0 100 100');
    expect(document.querySelectorAll('[aria-current="true"]')).toHaveLength(1);
  });

  it('marks a depleted gate with the amber disclosure dot and a distinct depleted badge', () => {
    inventory.data = [{ id: 7, name: 'PLA', material: 'PLA', remainingWeightG: 0, inUse: false }];
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status([gate(0), gate(1, { spoolId: 7 }), gate(2), gate(3)], { mmuType: MmuProtocol.Qidibox })}
        isOnline
        toolheads={threePersistedGates}
        reviewedRowVersion="rev-1"
      />,
    );

    expect(within(screen.getByTestId('filament-summary')).getByTestId('filament-empty-dot')).toBeInTheDocument();
    expect(screen.getByTestId('filament-summary')).toHaveAccessibleName(/1 at 0 g/);
    expect(within(screen.getByTestId('mmu-gate-1')).getByTestId('slot-badge-depleted')).toBeInTheDocument();
    expect(within(screen.getByTestId('mmu-gate-0')).queryByTestId('slot-badge-depleted')).not.toBeInTheDocument();
  });

  it('keeps the live color of a loaded gate whose assigned spool has 0 g instead of calling it empty', () => {
    inventory.data = [{ id: 7, name: 'ASA', material: 'ASA', remainingWeightG: 0, inUse: false }];
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(
          [gate(0), gate(1), gate(2, { spoolId: 7, material: 'ASA', color: '#808080', status: MmuGateStatus.Available }), gate(3)],
          { mmuType: MmuProtocol.Qidibox, activeGate: 2 },
        )}
        isOnline
        toolheads={threePersistedGates}
        reviewedRowVersion="rev-1"
      />,
    );

    const slot = screen.getByTestId('mmu-gate-2');
    expect(slot).toHaveAttribute('data-state', 'depleted');
    expect(within(slot).getByTestId('slot-badge-depleted')).toBeInTheDocument();
    expect(slot).toHaveAccessibleName('1C: ASA, 0 g remaining, in use');
    expect(slot).not.toHaveTextContent('Empty');
    expect(slot.querySelector('[data-stock="depleted"] svg [fill="#808080"]')).not.toBeNull();
  });

  it('calls a gate Empty when the sensor reports no filament even though material and color are retained', () => {
    inventory.data = [{ id: 7, name: 'ASA', material: 'ASA', remainingWeightG: 0, inUse: false }];
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(
          [gate(0), gate(1), gate(2, { spoolId: 7, material: 'ASA', color: '#808080', status: MmuGateStatus.Empty }), gate(3)],
          { mmuType: MmuProtocol.Qidibox, activeGate: 0 },
        )}
        isOnline
        toolheads={threePersistedGates}
        reviewedRowVersion="rev-1"
      />,
    );

    const slot = screen.getByTestId('mmu-gate-2');
    expect(slot).toHaveAccessibleName('1C: ASA, empty');
    expect(slot).toHaveTextContent('Empty');
    expect(slot).not.toHaveTextContent('0 g');
    expect(slot.querySelector('[data-stock="depleted"] svg [fill="#808080"]')).toBeNull();
  });

  it.each([
    { mmuType: MmuProtocol.Qidibox, physical: ['load', 'unload', 'eject'] },
    { mmuType: MmuProtocol.HappyHare, physical: ['load', 'unload'] },
    { mmuType: MmuProtocol.Afc, physical: ['load', 'unload'] },
  ])('offers the $mmuType panel only the physical commands the protocol supports', ({ mmuType, physical }) => {
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, { mmuType, activeGate: 0 })}
        isOnline
        toolheads={threePersistedGates}
        reviewedRowVersion="rev-1"
      />,
    );

    const panel = openSlot('mmu-gate-1');
    expect(panel).toHaveAccessibleName('Slot 1B');
    const kinds = within(panel).getAllByRole('button').map((b) => b.getAttribute('data-testid'));
    expect(kinds).toEqual(['slot-action-assign', ...physical.map((k) => `slot-action-${k}`)]);
    expect(action('load')).toHaveAccessibleName('Load slot 1B');
    // Happy Hare unloads whatever is loaded, so only the loaded gate offers it.
    if (mmuType === MmuProtocol.HappyHare) {
      expect(action('unload')).toHaveAttribute('aria-disabled', 'true');
      expect(action('unload')).toHaveAccessibleDescription('Unloads the loaded gate only');
    } else {
      expect(isBlocked(action('unload'))).toBe(false);
    }
    for (const button of within(panel).getAllByRole('button')) {
      expect(button).not.toHaveAttribute('title');
    }
  });

  it('gives an unknown protocol inventory controls only and never dispatches a physical command', async () => {
    const mmuMethods = [
      apiClient.mmuGateAction, apiClient.mmuChangeTool, apiClient.mmuEject, apiClient.mmuHome, apiClient.mmuRecover,
    ].map((fn) => vi.mocked(fn));
    for (const fn of mmuMethods) fn.mockClear();
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, {
          mmuType: MmuProtocol.Unknown, activeGate: 1, action: 'Error', isHomed: false, endlessSpool: true, clogDetection: true,
        })}
        isOnline
        toolheads={threePersistedGates}
        reviewedRowVersion="rev-1"
      />,
    );

    expect(screen.queryByRole('button', { name: 'Home the MMU' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Recover the MMU from error' })).not.toBeInTheDocument();
    expect(screen.queryByText('Not homed')).not.toBeInTheDocument();
    expect(screen.queryByText('Endless spool')).not.toBeInTheDocument();
    expect(screen.queryByText('Clog detection')).not.toBeInTheDocument();

    for (const gateId of ['mmu-gate-0', 'mmu-gate-1']) {
      const panel = openSlot(gateId);
      expect(within(panel).getAllByRole('button').map((b) => b.getAttribute('data-testid')))
        .toEqual(['slot-action-assign']);
      for (const kind of ['load', 'unload', 'eject']) expect(queryAction(kind)).not.toBeInTheDocument();
    }

    fireEvent.click(action('assign'));
    fireEvent.click(screen.getByTestId('spool-picker'));
    await waitFor(() => expect(setSpool).toHaveBeenCalledTimes(1));
    for (const fn of mmuMethods) expect(fn).not.toHaveBeenCalled();
  });

  it.each(['Load', 'Unload', 'Eject'] as const)('surfaces the server safety title for a rejected QidiBox %s', async (label) => {
    const title = 'The fresh measured hotend temperature is below the required minimum of 220 °C.';
    vi.mocked(apiClient.mmuGateAction).mockRejectedValue({
      statusCode: 409,
      message: 'Request failed with status code 409',
      data: { status: 409, title, code: 'printer_temperature_below_minimum' },
    });
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      render(
        <MmuControlBox
          printerId="printer-1"
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, activeGate: 1 })}
          isOnline
          toolheads={threePersistedGates}
          reviewedRowVersion="rev-1"
        />,
      );

      openSlot('mmu-gate-1');
      fireEvent.click(action(label.toLowerCase()));

      await waitFor(() => expect(toast.error).toHaveBeenCalledWith(title));
      expect(apiClient.mmuGateAction).toHaveBeenCalledExactlyOnceWith('printer-1', {
        protocol: 'Qidibox', action: label, gateIndex: 1,
      });
      await waitFor(() => expect(isBlocked(action(label.toLowerCase()))).toBe(false));
    } finally {
      consoleError.mockRestore();
    }
  });

  it.each([
    { error: { statusCode: 503, message: 'Printer is unavailable' }, expected: 'Printer is unavailable' },
    { error: new Error('Connection interrupted'), expected: 'Connection interrupted' },
    { error: null, expected: 'MMU Unload failed' },
  ])('preserves command error fallback: $expected', async ({ error, expected }) => {
    vi.mocked(apiClient.mmuGateAction).mockRejectedValue(error);
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      render(
        <MmuControlBox
          printerId="printer-1"
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, activeGate: 1 })}
          isOnline
        />,
      );

      openSlot('mmu-gate-1');
      fireEvent.click(action('unload'));

      await waitFor(() => expect(toast.error).toHaveBeenCalledWith(expected));
      await waitFor(() => expect(isBlocked(action('unload'))).toBe(false));
    } finally {
      consoleError.mockRestore();
    }
  });

  it('resolves gates by identity when telemetry arrives out of order', async () => {
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status([gate(1, { material: 'PETG' }), gate(0)], { mmuType: MmuProtocol.Qidibox, activeGate: 0 })}
        isOnline
        toolheads={twoGateToolheads}
        reviewedRowVersion="rev-1"
      />,
    );

    expect(screen.getByTestId('mmu-gate-1')).toHaveAccessibleName(/^1B: PETG/);
    openSlot('mmu-gate-0');
    fireEvent.click(action('assign'));
    fireEvent.click(screen.getByTestId('spool-picker'));

    await waitFor(() => expect(setSpool).toHaveBeenCalledWith(expect.objectContaining({ toolheadIndex: 1, spoolId: 99 })));
  });

  it('pins the picker to the slot it was opened for when the active gate changes', async () => {
    const props = {
      printerId: 'printer-1',
      isOnline: true,
      toolheads: twoGateToolheads,
      reviewedRowVersion: 'rev-1',
    };
    const { rerender } = render(
      <MmuControlBox {...props} mmuStatus={status([gate(0), gate(1)], { mmuType: MmuProtocol.Qidibox, activeGate: 0 })} />,
    );

    openSlot('mmu-gate-0');
    fireEvent.click(action('assign'));
    rerender(
      <MmuControlBox {...props} mmuStatus={status([gate(0), gate(1)], { mmuType: MmuProtocol.Qidibox, activeGate: 1 })} />,
    );
    fireEvent.click(screen.getByTestId('spool-picker'));

    await waitFor(() => expect(setSpool).toHaveBeenCalledWith(expect.objectContaining({ toolheadIndex: 1 })));
    expect(setSpool).not.toHaveBeenCalledWith(expect.objectContaining({ toolheadIndex: 2 }));
  });

  it('fails closed when the pinned slot disappears before confirmation', async () => {
    const props = {
      printerId: 'printer-1',
      isOnline: true,
      toolheads: twoGateToolheads,
      reviewedRowVersion: 'rev-1',
    };
    const { rerender } = render(
      <MmuControlBox {...props} mmuStatus={status([gate(0), gate(1)], { mmuType: MmuProtocol.Qidibox, activeGate: 1 })} />,
    );

    openSlot('mmu-gate-1');
    fireEvent.click(action('assign'));
    rerender(
      <MmuControlBox {...props} mmuStatus={status([gate(0)], { mmuType: MmuProtocol.Qidibox, activeGate: 0 })} />,
    );
    fireEvent.click(screen.getByTestId('spool-picker'));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect(setSpool).not.toHaveBeenCalled();
    expect(screen.queryByTestId('spool-picker')).not.toBeInTheDocument();
  });

  it('allows releasing a stale binding from a device-disabled gate', async () => {
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(
          [gate(0, { status: MmuGateStatus.Disabled }), gate(1)],
          { mmuType: MmuProtocol.Qidibox, activeGate: 0 },
        )}
        isOnline
        toolheads={[toolhead(0, 'Physical'), toolhead(1, 'MmuGate', { currentSpoolId: 80 }), toolhead(2, 'MmuGate')]}
        reviewedRowVersion="rev-1"
      />,
    );

    const panel = openSlot('mmu-gate-0');
    expect(within(panel).getByText('Disabled on device')).toBeInTheDocument();
    expect(action('change')).toHaveAttribute('aria-disabled', 'true');
    expect(action('load')).toHaveAttribute('aria-disabled', 'true');
    const release = action('unassign');
    expect(release).not.toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(release);

    await waitFor(() => expect(clearSpool).toHaveBeenCalledWith({
      printerId: 'printer-1',
      toolheadIndex: 1,
      reviewedRowVersion: 'rev-1',
    }));
  });

  it('says the layout is loading, without a retry, while toolheads are pending', () => {
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox })}
        isOnline
        reviewedRowVersion="rev-1"
      />,
    );

    expect(screen.getByRole('status')).toHaveTextContent('Loading saved gate layout');
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument();
  });

  it('offers a retry that refetches printer details on a layout mismatch', () => {
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox })}
        isOnline
        toolheads={[toolhead(0, 'Physical'), toolhead(1, 'Physical')]}
        reviewedRowVersion="rev-1"
      />,
    );

    expect(screen.getByRole('status')).toHaveTextContent(/does not match/);
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['printers', 'printer-1', 'details'] });
  });

  it('surfaces runout risk on the affected gate', () => {
    coverage.mockReturnValue({
      printerId: 'printer-1',
      printerName: 'qp4-1',
      activeJobId: 'job-1',
      status: 'runout',
      toolheads: [{ toolheadIndex: 2, status: 'runout', statusReason: null, remainingGrams: 100, totalDemandGrams: 400 }],
    });
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox })}
        isOnline
        toolheads={threePersistedGates}
        reviewedRowVersion="rev-1"
      />,
    );

    const atRisk = screen.getByTestId('mmu-gate-2');
    expect(atRisk).toHaveAccessibleName(/^1C:.*runout risk$/);
    expect(atRisk).toHaveAttribute('data-status', 'runout');
    expect(screen.getByTestId('mmu-gate-0')).toHaveAttribute('data-status', 'unknown');
    const panel = openSlot('mmu-gate-2');
    expect(within(panel).getByText('Needs 400 g')).toBeInTheDocument();
  });

  it('does not join coverage onto gates when the saved layout is unresolved', () => {
    coverage.mockReturnValue({
      printerId: 'printer-1',
      printerName: 'qp4-1',
      activeJobId: 'job-1',
      status: 'runout',
      toolheads: [{ toolheadIndex: 1, status: 'runout', statusReason: null, remainingGrams: 100, totalDemandGrams: 400 }],
    });
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox })}
        isOnline
        toolheads={[toolhead(0, 'Physical'), toolhead(2, 'MmuGate'), toolhead(3, 'MmuGate')]}
        reviewedRowVersion="rev-1"
      />,
    );

    for (const index of [0, 1, 2, 3]) {
      expect(screen.getByTestId(`mmu-gate-${index}`)).toHaveAttribute('data-status', 'unknown');
    }
  });

  it('does not attribute coverage to gates whose saved layout is non-canonical', () => {
    // Equal counts resolve positionally: live 0 -> Toolhead 2, live 1 -> Toolhead 5.
    // Coverage is keyed per backend tool, so live keys 0/1 are not those gates.
    coverage.mockReturnValue({
      printerId: 'printer-1',
      printerName: 'qp4-1',
      activeJobId: 'job-1',
      status: 'runout',
      toolheads: [{ toolheadIndex: 1, status: 'runout', statusReason: null, remainingGrams: 100, totalDemandGrams: 400 }],
    });
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status([gate(0), gate(1)], { mmuType: MmuProtocol.Qidibox })}
        isOnline
        toolheads={[toolhead(0, 'Physical'), toolhead(2, 'MmuGate'), toolhead(5, 'MmuGate')]}
        reviewedRowVersion="rev-1"
      />,
    );

    openSlot('mmu-gate-0');
    expect(isBlocked(action('assign'))).toBe(false);
    for (const index of [0, 1]) {
      expect(screen.getByTestId(`mmu-gate-${index}`)).toHaveAttribute('data-status', 'unknown');
    }
  });

  it('keeps the newest own-write revision across chained writes and late refreshes', async () => {
    clearSpool.mockReset()
      .mockResolvedValueOnce('rev-2')
      .mockResolvedValueOnce('rev-3')
      .mockResolvedValueOnce('rev-4')
      .mockResolvedValueOnce('rev-5');
    const props = {
      printerId: 'printer-1',
      mmuStatus: status([gate(0), gate(1)], { mmuType: MmuProtocol.Qidibox, activeGate: 0 }),
      isOnline: true,
      toolheads: [
        toolhead(0, 'Physical'),
        toolhead(1, 'MmuGate', { currentSpoolId: 80 }),
        toolhead(2, 'MmuGate', { currentSpoolId: 81 }),
      ],
    };
    const release = async (gateIndex: number, expectedRevision: string, call: number) => {
      // Close any open panel first so each release re-anchors on open.
      if (screen.queryByTestId('slot-panel')) fireEvent.keyDown(screen.getByTestId('slot-panel'), { key: 'Escape' });
      openSlot(`mmu-gate-${gateIndex}`);
      fireEvent.click(action('unassign'));
      await waitFor(() => expect(clearSpool).toHaveBeenCalledTimes(call));
      expect(clearSpool).toHaveBeenLastCalledWith({
        printerId: 'printer-1',
        toolheadIndex: gateIndex + 1,
        reviewedRowVersion: expectedRevision,
      });
    };
    const { rerender } = render(<MmuControlBox {...props} reviewedRowVersion="rev-1" />);

    await release(0, 'rev-1', 1);
    await release(1, 'rev-2', 2);
    // Refresh for the first write lands after the second write returned rev-3.
    rerender(<MmuControlBox {...props} reviewedRowVersion="rev-2" />);
    await release(0, 'rev-3', 3);
    // Refresh catching up to our latest write releases the anchor without change.
    rerender(<MmuControlBox {...props} reviewedRowVersion="rev-4" />);
    await release(1, 'rev-4', 4);
    // A token our chain never saw comes from a later external write and wins.
    rerender(<MmuControlBox {...props} reviewedRowVersion="ext-9" />);
    await release(0, 'ext-9', 5);
  });

  const scopedProps = {
    mmuStatus: status([gate(0), gate(1)], { mmuType: MmuProtocol.Qidibox, activeGate: 0 }),
    isOnline: true,
    toolheads: [
      toolhead(0, 'Physical'),
      toolhead(1, 'MmuGate', { currentSpoolId: 80 }),
      toolhead(2, 'MmuGate', { currentSpoolId: 81 }),
    ],
  };
  const reopenGate0 = () => {
    if (screen.queryByTestId('slot-panel')) fireEvent.keyDown(screen.getByTestId('slot-panel'), { key: 'Escape' });
    openSlot('mmu-gate-0');
  };

  it('does not carry one printer\'s own-write revision onto another printer', async () => {
    const { rerender } = render(
      <MmuControlBox {...scopedProps} printerId="printer-1" reviewedRowVersion="rev-1" />,
    );
    reopenGate0();
    fireEvent.click(action('unassign'));
    await waitFor(() => expect(clearSpool).toHaveBeenCalledTimes(1));

    // Same component instance switches printers before B's revision is known.
    rerender(<MmuControlBox {...scopedProps} printerId="printer-2" reviewedRowVersion={undefined} />);
    reopenGate0();
    expect(action('change')).toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(action('unassign'));
    fireEvent.click(action('change'));
    expect(clearSpool).toHaveBeenCalledTimes(1);
    expect(setSpool).not.toHaveBeenCalled();

    rerender(<MmuControlBox {...scopedProps} printerId="printer-2" reviewedRowVersion="b-1" />);
    reopenGate0();
    fireEvent.click(action('unassign'));
    await waitFor(() => expect(clearSpool).toHaveBeenCalledTimes(2));
    expect(clearSpool).toHaveBeenLastCalledWith({ printerId: 'printer-2', toolheadIndex: 1, reviewedRowVersion: 'b-1' });
  });

  it('ignores a previous printer\'s write that resolves after switching printers', async () => {
    let resolveA: (revision: string) => void = () => {};
    clearSpool.mockReset()
      .mockImplementationOnce(() => new Promise<string>((resolve) => { resolveA = resolve; }))
      .mockResolvedValueOnce('b-2');
    const { rerender } = render(
      <MmuControlBox {...scopedProps} printerId="printer-1" reviewedRowVersion="rev-1" />,
    );
    reopenGate0();
    fireEvent.click(action('unassign'));
    await waitFor(() => expect(clearSpool).toHaveBeenCalledTimes(1));

    rerender(<MmuControlBox {...scopedProps} printerId="printer-2" reviewedRowVersion="b-1" />);
    resolveA('rev-2');
    await Promise.resolve();
    reopenGate0();
    await waitFor(() => expect(isBlocked(action('unassign'))).toBe(false));
    fireEvent.click(action('unassign'));
    await waitFor(() => expect(clearSpool).toHaveBeenCalledTimes(2));
    expect(clearSpool).toHaveBeenLastCalledWith({ printerId: 'printer-2', toolheadIndex: 1, reviewedRowVersion: 'b-1' });
  });

  it('uses the shared recognizable spool silhouette with themed frame and a through-hole', () => {
    const { container } = render(
      <MmuControlBox printerId="printer-1" mmuStatus={status([gate(0, { spoolId: 1 })])} isOnline />,
    );

    const hubs = container.querySelectorAll('circle[cx="50"][cy="50"][r="12"]');
    expect(hubs.length).toBeGreaterThan(0);
    for (const hub of hubs) {
      expect(hub).toHaveAttribute('fill', 'none');
      expect(hub).toHaveAttribute('stroke', 'var(--pf-text-secondary)');
    }
  });

  it('assigns a spool to the selected QidiBox slot through the shared picker', async () => {
    const onSpoolChange = vi.fn();
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, activeGate: 3 })}
        isOnline
        toolheads={threePersistedGates}
        reviewedRowVersion="rev-1"
        onSpoolChange={onSpoolChange}
      />,
    );

    openSlot('mmu-gate-3');
    const assign = action('assign');
    expect(assign).toHaveAccessibleName('Assign spool to slot 1D');
    expect(isBlocked(assign)).toBe(false);

    fireEvent.click(assign);
    fireEvent.click(screen.getByTestId('spool-picker'));

    // Live gate 3 (4th slot) maps to persisted index 4, which the backend gap-fills.
    await waitFor(() => expect(setSpool).toHaveBeenCalledWith({
      printerId: 'printer-1',
      toolheadIndex: 4,
      spoolId: 99,
      reviewedRowVersion: 'rev-1',
    }));
    expect(onSpoolChange).toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByTestId('spool-picker')).not.toBeInTheDocument());
  });

  it('assigns spools even while the printer is offline (bookkeeping, not a device command)', () => {
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox })}
        isOnline={false}
        toolheads={threePersistedGates}
        reviewedRowVersion="rev-1"
      />,
    );

    openSlot('mmu-gate-0');
    expect(action('load')).toHaveAttribute('aria-disabled', 'true');
    expect(action('load')).toHaveAccessibleDescription('Printer is offline');
    expect(isBlocked(action('assign'))).toBe(false);
  });

  it('routes the picker eject action to clear and labels a bound slot Change', async () => {
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status([gate(0, { spoolId: 80 }), gate(1)], { mmuType: MmuProtocol.Qidibox })}
        isOnline
        toolheads={[toolhead(0, 'Physical'), toolhead(1, 'MmuGate'), toolhead(2, 'MmuGate')]}
        reviewedRowVersion="rev-1"
      />,
    );

    openSlot('mmu-gate-0');
    expect(queryAction('assign')).not.toBeInTheDocument();
    expect(action('change')).toHaveAccessibleName('Change spool in slot 1A');
    fireEvent.click(action('change'));
    fireEvent.click(screen.getByTestId('spool-picker-eject'));

    await waitFor(() => expect(clearSpool).toHaveBeenCalledWith({
      printerId: 'printer-1',
      toolheadIndex: 1,
      reviewedRowVersion: 'rev-1',
    }));
    expect(setSpool).not.toHaveBeenCalled();
  });

  it('blocks assignment when the saved layout cannot be mapped to the hardware', () => {
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox })}
        isOnline
        toolheads={[toolhead(0, 'Physical'), toolhead(1, 'Physical')]}
        reviewedRowVersion="rev-1"
      />,
    );

    openSlot('mmu-gate-0');
    const assign = action('assign');
    expect(assign).toHaveAttribute('aria-disabled', 'true');
    expect(assign).toHaveAccessibleDescription(/Saved gate layout does not match/);
    expect(assign).not.toHaveAttribute('title');
    fireEvent.click(assign);
    expect(screen.queryByTestId('spool-picker')).not.toBeInTheDocument();
  });

  it('offers the printer LOAD/UNLOAD macros from the slot panel only while the unit is disabled', () => {
    const onAction = vi.fn();
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, enabled: false })}
        isOnline
        toolheads={threePersistedGates}
        reviewedRowVersion="rev-1"
        filamentControl={{ onAction }}
      />,
    );

    openSlot('mmu-gate-0');
    expect(queryAction('eject')).not.toBeInTheDocument();
    fireEvent.click(action('unload'));
    expect(onAction).toHaveBeenCalledWith('unload');
    expect(apiClient.mmuGateAction).not.toHaveBeenCalled();
  });

  describe('QidiBox Rack (external spool holder)', () => {
    // Live qp4-1 shape: Rack spool persisted on physical toolhead 0, box slots on 1..3.
    const rackToolheads = [
      toolhead(0, 'Physical', { currentSpoolId: 127, currentMaterial: 'ASA', currentFilamentColor: '#222222' }),
      toolhead(1, 'MmuGate', { currentSpoolId: 80 }),
      toolhead(2, 'MmuGate', { currentSpoolId: 39 }),
      toolhead(3, 'MmuGate', { currentSpoolId: 111 }),
    ];
    const props = {
      printerId: 'printer-1',
      isOnline: true,
      reviewedRowVersion: 'rev-1',
    };

    it('hides the Rack by default, even when the device reports one', () => {
      render(
        <MmuControlBox
          {...props}
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 1 })}
        />,
      );

      expect(screen.queryByTestId('mmu-rack')).not.toBeInTheDocument();
      expect(screen.queryByText('Rack')).not.toBeInTheDocument();
      expect(railSlots()).toHaveLength(4);
    });

    it('still names the hidden Rack on the resting line when it is the active feed, without touching its binding', () => {
      render(
        <MmuControlBox
          {...props}
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, activeGate: -2, activeTool: -2, hasBypass: true })}
        />,
      );

      expect(screen.getByTestId('filament-summary-line')).toHaveTextContent(/Rack·ASA/);
      expect(screen.queryByTestId('mmu-rack')).not.toBeInTheDocument();
      expect(document.querySelectorAll('[aria-current="true"]')).toHaveLength(0);
      expect(setSpool).not.toHaveBeenCalled();
      expect(clearSpool).not.toHaveBeenCalled();
    });

    it('shows Rack before the four gates when enabled, marking only the loaded gate', () => {
      render(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 1 })}
        />,
      );

      expect(railSlots().map((el) => el.getAttribute('data-testid')))
        .toEqual(['mmu-rack', 'mmu-gate-0', 'mmu-gate-1', 'mmu-gate-2', 'mmu-gate-3']);
      expect(screen.getByTestId('mmu-rack')).toHaveAccessibleName(/^Rack: ASA/);
      expect(screen.getByTestId('mmu-rack')).not.toHaveAttribute('data-active');
      expect(screen.getByTestId('mmu-gate-1')).toHaveAttribute('aria-current', 'true');
      expect(document.querySelectorAll('[aria-current="true"]')).toHaveLength(1);
    });

    it('shows the live Rack material and color instead of the stale persisted toolhead', () => {
      render(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 1, bypassMaterial: 'PETG', bypassColor: '#00aa55' })}
        />,
      );

      const rack = screen.getByTestId('mmu-rack');
      expect(rack).toHaveAccessibleName(/^Rack: PETG/);
      expect(rack.querySelector('svg [fill="#00aa55"]')).not.toBeNull();
    });

    it('selecting Rack opens the picker and binds physical toolhead 0, not a gate', async () => {
      render(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={[toolhead(0, 'Physical'), ...rackToolheads.slice(1)]}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 1 })}
        />,
      );

      expect(screen.getByTestId('mmu-rack')).toHaveAttribute('data-state', 'none');
      const panel = openSlot('mmu-rack');
      expect(panel).toHaveAccessibleName('Rack');
      expect(action('assign')).toHaveAccessibleName('Assign spool to the rack');
      fireEvent.click(action('assign'));
      fireEvent.click(screen.getByTestId('spool-picker'));

      await waitFor(() => expect(setSpool).toHaveBeenCalledWith(
        expect.objectContaining({ toolheadIndex: 0, spoolId: 99 }),
      ));
      expect(setSpool).toHaveBeenCalledTimes(1);
    });

    it('releases the Rack spool from toolhead 0 without touching a gate', async () => {
      render(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 0 })}
        />,
      );

      const panel = openSlot('mmu-rack');
      expect(within(panel).getByText('Spool #127')).toBeInTheDocument();
      expect(within(panel).queryByTestId('slot-device-name')).not.toBeInTheDocument();
      expect(isBlocked(action('change'))).toBe(false);
      fireEvent.click(action('unassign'));

      await waitFor(() => expect(clearSpool).toHaveBeenCalledWith({
        printerId: 'printer-1',
        toolheadIndex: 0,
        reviewedRowVersion: 'rev-1',
      }));
    });

    it('offers only inventory actions for the Rack, since box commands cannot reach it', () => {
      render(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 2 })}
        />,
      );

      openSlot('mmu-rack');
      for (const kind of ['load', 'unload', 'eject']) {
        expect(queryAction(kind)).not.toBeInTheDocument();
      }
      expect(screen.getByTestId('mmu-rack')).toHaveAttribute('aria-expanded', 'true');
      expect(apiClient.mmuGateAction).not.toHaveBeenCalled();
    });

    it('keeps Rack selectable and pinned across active-gate transitions, including unloaded', async () => {
      const { rerender } = render(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 0 })}
        />,
      );

      openSlot('mmu-rack');
      fireEvent.click(action('change'));
      rerender(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: -1, filamentState: 'Unloaded' })}
        />,
      );
      expect(screen.getByTestId('mmu-rack')).toBeInTheDocument();
      expect(document.querySelectorAll('[aria-current="true"]')).toHaveLength(0);
      fireEvent.click(screen.getByTestId('spool-picker'));

      await waitFor(() => expect(setSpool).toHaveBeenCalledWith(expect.objectContaining({ toolheadIndex: 0 })));
    });

    it('selecting a box slot after Rack targets that gate again', async () => {
      render(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 0 })}
        />,
      );

      openSlot('mmu-rack');
      const panel = openSlot('mmu-gate-2');
      expect(panel).toHaveAccessibleName('Slot 1C');
      fireEvent.click(action('change'));
      fireEvent.click(screen.getByTestId('spool-picker'));

      await waitFor(() => expect(setSpool).toHaveBeenCalledWith(expect.objectContaining({ toolheadIndex: 3 })));
    });

    it('marks a visible Rack as in use when the QidiBox feeds from it (activeGate -2)', () => {
      render(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, activeGate: -2, activeTool: -2, hasBypass: true })}
        />,
      );

      const rack = screen.getByTestId('mmu-rack');
      expect(rack).toHaveAccessibleName(/^Rack: ASA.*in use/);
      expect(rack).toHaveAttribute('data-active', 'loaded');
      expect(rack).toHaveAttribute('aria-current', 'true');
      expect(screen.getByTestId('filament-summary-line')).toHaveTextContent(/Rack·ASA/);
      expect(document.querySelectorAll('[aria-current="true"]')).toHaveLength(1);
    });

    it('does not mark Rack in use when the active gate is unknown (-1), falling back to the mini rail', () => {
      render(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: -1, filamentState: 'Unloaded' })}
        />,
      );

      expect(screen.getByTestId('mmu-rack')).not.toHaveAttribute('aria-current');
      expect(screen.queryByTestId('filament-summary-line')).not.toBeInTheDocument();
      expect(screen.getByTestId('filament-summary-mini')).toBeInTheDocument();
    });

    it('hides the Rack when the device reports no bypass holder', () => {
      render(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, activeGate: -2, activeTool: -2, hasBypass: false })}
        />,
      );

      expect(screen.queryByTestId('mmu-rack')).not.toBeInTheDocument();
      expect(screen.queryByTestId('filament-summary-line')).not.toBeInTheDocument();
    });

    it('does not surface a Rack for non-QidiBox units or ambiguous physical toolheads', () => {
      const { rerender } = render(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.HappyHare, activeGate: -1 })}
        />,
      );
      expect(screen.queryByTestId('mmu-rack')).not.toBeInTheDocument();

      rerender(
        <MmuControlBox
          {...props}
          showExternalSpool
          toolheads={[...rackToolheads, toolhead(4, 'Physical')]}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: -1 })}
        />,
      );
      expect(screen.queryByTestId('mmu-rack')).not.toBeInTheDocument();
    });
  });
});


it('keeps selection separate from physical in-use and quantity on QidiBox', () => {
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  permissions.assign = permissions.operate = true;
  inventory.isError = false;
  inventory.data = [{ id: 7, name: 'PLA', material: 'PLA', remainingWeightG: 0, inUse: false }];
  render(<MmuControlBox printerId="printer-1" isOnline reviewedRowVersion="rev-1" toolheads={threePersistedGates}
    mmuStatus={status([gate(0), gate(1, { spoolId: 7 }), gate(2, { spoolId: 8, status: MmuGateStatus.Empty }), gate(3)], { mmuType: MmuProtocol.Qidibox })} />);
  const loaded = screen.getByTestId('mmu-gate-0');
  const depleted = screen.getByTestId('mmu-gate-1');
  expect(depleted).toHaveAttribute('data-state', 'depleted');
  fireEvent.click(depleted);
  expect(depleted).toHaveAttribute('aria-expanded', 'true');
  expect(depleted).not.toHaveAttribute('aria-current');
  expect(loaded).toHaveAttribute('aria-current', 'true');
  // A device-empty gate keeps its binding; feed state does not fake depletion.
  expect(screen.getByTestId('mmu-gate-2')).toHaveAttribute('data-state', 'assigned');
  expect(document.querySelectorAll('[aria-current="true"]')).toHaveLength(1);
});


it.each(['start', 'write'])('respects the separate queue:%s permission', (permission) => {
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  permissions.operate = permission !== 'start';
  permissions.assign = permission !== 'write';
  inventory.data = undefined;
  inventory.isError = false;
  render(<MmuControlBox printerId="printer-1" isOnline reviewedRowVersion="rev-1" toolheads={threePersistedGates}
    mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox })} />);
  fireEvent.click(screen.getByTestId('mmu-gate-0'));
  const panel = screen.getByTestId('slot-panel');
  const load = within(panel).getByTestId('slot-action-load');
  const assign = within(panel).getByTestId('slot-action-assign');
  if (permission === 'start') {
    // Disabled-by-permission stays focusable and explains itself.
    expect(load).toHaveAttribute('aria-disabled', 'true');
    expect(load).not.toBeDisabled();
    expect(load).toHaveAccessibleDescription(/permission/);
    expect(assign).not.toHaveAttribute('aria-disabled', 'true');
  } else {
    expect(assign).toHaveAttribute('aria-disabled', 'true');
    expect(load).not.toHaveAttribute('aria-disabled', 'true');
    expect(load).toBeEnabled();
  }
});


it('preserves recovery during an apparently pending MMU action', async () => {
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  permissions.assign = permissions.operate = true;
  let complete: () => void = () => {};
  vi.mocked(apiClient.mmuEject).mockImplementation(() => new Promise((resolve) => { complete = () => resolve({ success: true, message: 'Fixture complete' }); }));
  render(<MmuControlBox printerId="printer-1" isOnline reviewedRowVersion="rev-1" toolheads={threePersistedGates}
    mmuStatus={status(qidiGates, { mmuType: MmuProtocol.HappyHare, action: 'Error' })} />);
  fireEvent.click(screen.getByTestId('mmu-gate-0'));
  const panel = screen.getByTestId('slot-panel');
  fireEvent.click(within(panel).getByTestId('slot-action-unload'));
  await waitFor(() => expect(within(panel).getByTestId('slot-action-load')).toHaveAttribute('aria-disabled', 'true'));
  const recover = screen.getByRole('button', { name: 'Recover the MMU from error' });
  expect(recover).not.toHaveAttribute('aria-disabled', 'true');
  expect(recover).toBeEnabled();
  complete();
  await waitFor(() => expect(within(panel).getByTestId('slot-action-load')).not.toHaveAttribute('aria-disabled', 'true'));
});
