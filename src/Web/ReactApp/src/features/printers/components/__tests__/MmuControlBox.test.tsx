
const permissions = vi.hoisted(() => ({ assign: true, operate: true }));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: (_resource: string, action: string) => action === 'write' ? permissions.assign : permissions.operate }),
}));
const inventory = vi.hoisted(() => ({ data: undefined as import('@/types/api').SpoolmanSpool[] | undefined, isError: false, refetch: vi.fn() }));
vi.mock('@/features/printers/hooks/usePrinterSpoolInventory', () => ({ usePrinterSpoolInventory: () => inventory }));
import type { ReactElement } from 'react';
import { fireEvent, render as rtlRender, screen, waitFor } from '@testing-library/react';
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

vi.mock('@/services/api', () => ({ apiClient: { mmuGateAction: vi.fn(), mmuEject: vi.fn() } }));

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

  it('shows only the four QidiBox gate tiles without an orphan divider when Rack is absent', () => {
    render(
      <MmuControlBox
        printerId="printer-1"
        mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, activeGate: 1 })}
        isOnline
        toolheads={threePersistedGates}
        reviewedRowVersion="rev-1"
      />,
    );

    const activeGate = screen.getByRole('button', { name: /^Gate 1B:/ });
    expect(activeGate).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getAllByText('In use')).toHaveLength(1);
    expect(screen.queryByText('Rack')).not.toBeInTheDocument();
    const gates = screen.getAllByRole('button', { name: /^Gate 1[A-D]:/ });
    expect(gates).toHaveLength(4);
    expect(Array.from(activeGate.parentElement!.children)).toEqual(gates);
  });

  it.each([MmuProtocol.HappyHare, MmuProtocol.Afc])('marks the actual feed without a duplicate Rack for %s', (mmuType) => {
    render(<MmuControlBox printerId="printer-1" mmuStatus={status([gate(0, { material: 'PETG' }), gate(1)], { mmuType })} isOnline />);
    expect(screen.queryByText('Rack')).not.toBeInTheDocument();
    const active = screen.getByRole('button', { name: /^Gate 1A:/ });
    expect(active).toHaveTextContent('PETG');
    expect(active).toHaveTextContent('In use');
    expect(active.querySelector('svg')).toHaveAttribute('viewBox', '0 0 100 100');
    expect(screen.getAllByText('In use')).toHaveLength(1);
  });

  it.each(['Load', 'Unload', 'Eject'] as const)('surfaces the server safety title for a rejected QidiBox %s', async (action) => {
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

      fireEvent.click(screen.getByRole('button', { name: action }));

      await waitFor(() => expect(toast.error).toHaveBeenCalledWith(title));
      expect(apiClient.mmuGateAction).toHaveBeenCalledExactlyOnceWith('printer-1', {
        protocol: 'Qidibox', action, gateIndex: 1,
      });
      expect(screen.getByRole('button', { name: action })).toBeEnabled();
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

      fireEvent.click(screen.getByRole('button', { name: 'Unload' }));

      await waitFor(() => expect(toast.error).toHaveBeenCalledWith(expected));
      expect(screen.getByRole('button', { name: 'Unload' })).toBeEnabled();
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

    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
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

    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
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

    fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
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

    expect(screen.getByRole('button', { name: /^(Assign|Change)$/ })).toHaveAttribute('aria-disabled', 'true');
    const release = screen.getByRole('button', { name: 'Unassign' });
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

    const atRisk = screen.getByRole('button', { name: /^Gate 1C:.*runout risk$/ });
    expect(atRisk).toHaveAttribute('data-status', 'runout');
    expect(screen.getByRole('button', { name: /^Gate 1A:/ })).toHaveAttribute('data-status', 'unknown');
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

    for (const name of [/^Gate 1A:/, /^Gate 1B:/, /^Gate 1C:/, /^Gate 1D:/]) {
      expect(screen.getByRole('button', { name })).toHaveAttribute('data-status', 'unknown');
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

    expect(screen.getByRole('button', { name: 'Assign' })).toBeEnabled();
    for (const name of [/^Gate 1A:/, /^Gate 1B:/]) {
      expect(screen.getByRole('button', { name })).toHaveAttribute('data-status', 'unknown');
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
    const release = async (gateName: RegExp, expectedRevision: string, call: number) => {
      fireEvent.click(screen.getByRole('button', { name: gateName }));
      fireEvent.click(screen.getByRole('button', { name: 'Unassign' }));
      await waitFor(() => expect(clearSpool).toHaveBeenCalledTimes(call));
      expect(clearSpool).toHaveBeenLastCalledWith({
        printerId: 'printer-1',
        toolheadIndex: /1A/.test(gateName.source) ? 1 : 2,
        reviewedRowVersion: expectedRevision,
      });
    };
    const { rerender } = render(<MmuControlBox {...props} reviewedRowVersion="rev-1" />);

    await release(/^Gate 1A:/, 'rev-1', 1);
    await release(/^Gate 1B:/, 'rev-2', 2);
    // Refresh for the first write lands after the second write returned rev-3.
    rerender(<MmuControlBox {...props} reviewedRowVersion="rev-2" />);
    await release(/^Gate 1A:/, 'rev-3', 3);
    // Refresh catching up to our latest write releases the anchor without change.
    rerender(<MmuControlBox {...props} reviewedRowVersion="rev-4" />);
    await release(/^Gate 1B:/, 'rev-4', 4);
    // A token our chain never saw comes from a later external write and wins.
    rerender(<MmuControlBox {...props} reviewedRowVersion="ext-9" />);
    await release(/^Gate 1A:/, 'ext-9', 5);
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

  it('does not carry one printer\'s own-write revision onto another printer', async () => {
    const { rerender } = render(
      <MmuControlBox {...scopedProps} printerId="printer-1" reviewedRowVersion="rev-1" />,
    );
    fireEvent.click(screen.getByRole('button', { name: /^Gate 1A:/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Unassign' }));
    await waitFor(() => expect(clearSpool).toHaveBeenCalledTimes(1));

    // Same component instance switches printers before B's revision is known.
    rerender(<MmuControlBox {...scopedProps} printerId="printer-2" reviewedRowVersion={undefined} />);
    fireEvent.click(screen.getByRole('button', { name: /^Gate 1A:/ }));
    expect(screen.getByRole('button', { name: /^(Assign|Change)$/ })).toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Unassign' }));
    fireEvent.click(screen.getByRole('button', { name: /^(Assign|Change)$/ }));
    expect(clearSpool).toHaveBeenCalledTimes(1);
    expect(setSpool).not.toHaveBeenCalled();

    rerender(<MmuControlBox {...scopedProps} printerId="printer-2" reviewedRowVersion="b-1" />);
    fireEvent.click(screen.getByRole('button', { name: /^Gate 1A:/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Unassign' }));
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
    fireEvent.click(screen.getByRole('button', { name: /^Gate 1A:/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Unassign' }));
    await waitFor(() => expect(clearSpool).toHaveBeenCalledTimes(1));

    rerender(<MmuControlBox {...scopedProps} printerId="printer-2" reviewedRowVersion="b-1" />);
    resolveA('rev-2');
    await Promise.resolve();
    fireEvent.click(screen.getByRole('button', { name: /^Gate 1A:/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Unassign' }));
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

  it('offers Assign beside Eject/Unload/Load and binds the selected QidiBox slot', async () => {
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

    for (const label of ['Eject', 'Unload', 'Load']) {
      expect(screen.getByRole('button', { name: label })).toBeInTheDocument();
    }
    const assign = screen.getByRole('button', { name: 'Assign' });
    expect(assign).toBeEnabled();

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

    expect(screen.getByRole('button', { name: 'Load' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Assign' })).toBeEnabled();
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

    fireEvent.click(screen.getByRole('button', { name: 'Change' }));
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

    const assign = screen.getByRole('button', { name: 'Assign' });
    expect(assign).toHaveAttribute('title', expect.stringMatching(/Saved gate layout does not match/));
    fireEvent.click(assign);
    expect(screen.queryByTestId('spool-picker')).not.toBeInTheDocument();
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

    it('shows Rack and four gates without a duplicate In use tile while a box gate is active', () => {
      render(
        <MmuControlBox
          {...props}
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 1 })}
        />,
      );

      const rack = screen.getByRole('button', { name: /^Rack: ASA, Spool #127/ });
      expect(screen.getAllByText('Rack')).toHaveLength(1);
      expect(rack).not.toHaveAttribute('data-active');
      expect(screen.getAllByText('In use')).toHaveLength(1);
      const gates = screen.getAllByRole('button', { name: /^Gate 1[A-D]:/ });
      expect(gates).toHaveLength(4);
      for (const gateTile of gates) {
        expect(gateTile).toHaveAttribute('aria-pressed', String(gateTile === gates[1]));
      }
      expect(rack.parentElement!.children).toHaveLength(5);
    });

    it('selecting Rack opens the picker and binds physical toolhead 0, not a gate', async () => {
      render(
        <MmuControlBox
          {...props}
          toolheads={[toolhead(0, 'Physical'), ...rackToolheads.slice(1)]}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 1 })}
        />,
      );

      fireEvent.click(screen.getByRole('button', { name: /^Rack: Unknown material, No spool assigned/ }));
      expect(screen.getByText('Rack (external spool)')).toBeInTheDocument();
      fireEvent.click(screen.getByRole('button', { name: 'Assign' }));
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
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 0 })}
        />,
      );

      fireEvent.click(screen.getByRole('button', { name: /^Rack: ASA, Spool #127/ }));
      expect(screen.getByText('#127')).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Change' })).not.toHaveAttribute('aria-disabled', 'true');
      fireEvent.click(screen.getByRole('button', { name: 'Unassign' }));

      await waitFor(() => expect(clearSpool).toHaveBeenCalledWith({
        printerId: 'printer-1',
        toolheadIndex: 0,
        reviewedRowVersion: 'rev-1',
      }));
    });

    it('keeps box-slot commands off the loaded gate while Rack is selected', () => {
      render(
        <MmuControlBox
          {...props}
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 2 })}
        />,
      );

      fireEvent.click(screen.getByRole('button', { name: /^Rack:/ }));
      for (const name of ['Eject', 'Unload', 'Load']) {
        expect(screen.getByRole('button', { name })).toBeDisabled();
      }
      expect(screen.getByRole('button', { name: /^Rack:/ })).toHaveAttribute('aria-pressed', 'true');
    });

    it('keeps Rack selectable and pinned across active-gate transitions, including unloaded', async () => {
      const { rerender } = render(
        <MmuControlBox
          {...props}
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 0 })}
        />,
      );

      fireEvent.click(screen.getByRole('button', { name: /^Rack:/ }));
      fireEvent.click(screen.getByRole('button', { name: 'Change' }));
      rerender(
        <MmuControlBox
          {...props}
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: -1, filamentState: 'Unloaded' })}
        />,
      );
      expect(screen.getByRole('button', { name: /^Rack:/ })).toBeInTheDocument();
      expect(screen.queryByText('In use')).not.toBeInTheDocument();
      fireEvent.click(screen.getByTestId('spool-picker'));

      await waitFor(() => expect(setSpool).toHaveBeenCalledWith(expect.objectContaining({ toolheadIndex: 0 })));
    });

    it('selecting a box slot after Rack targets that gate again', async () => {
      render(
        <MmuControlBox
          {...props}
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: 0 })}
        />,
      );

      fireEvent.click(screen.getByRole('button', { name: /^Rack:/ }));
      fireEvent.click(screen.getByRole('button', { name: /^Gate 1C:/ }));
      expect(screen.queryByText('Rack (external spool)')).not.toBeInTheDocument();
      fireEvent.click(screen.getByRole('button', { name: 'Change' }));
      fireEvent.click(screen.getByTestId('spool-picker'));

      await waitFor(() => expect(setSpool).toHaveBeenCalledWith(expect.objectContaining({ toolheadIndex: 3 })));
    });

    it('marks Rack as in use and shows it by default when the QidiBox feeds from it (activeGate -2)', () => {
      render(
        <MmuControlBox
          {...props}
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, activeGate: -2, activeTool: -2, hasBypass: true })}
        />,
      );

      const rack = screen.getByRole('button', { name: /^Rack: ASA, Spool #127.*In use$/ });
      expect(rack).toHaveAttribute('aria-pressed', 'true');
      expect(rack).toHaveAttribute('data-active', 'loaded');
      expect(screen.getByText('In use')).toBeInTheDocument();
      expect(screen.getByText('Rack (external spool)')).toBeInTheDocument();
      expect(screen.getByText(/Feeding from/)).toBeInTheDocument();
      for (const name of ['Eject', 'Unload', 'Load']) {
        expect(screen.getByRole('button', { name })).toBeDisabled();
      }

      fireEvent.click(screen.getByRole('button', { name: /^Gate 1A:/ }));
      expect(screen.queryByText('Rack (external spool)')).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: /^Rack:.*In use$/ })).toHaveAttribute('aria-pressed', 'false');
    });

    it('does not mark Rack in use when the active gate is unknown (-1)', () => {
      render(
        <MmuControlBox
          {...props}
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: -1, filamentState: 'Unloaded' })}
        />,
      );

      const rack = screen.getByRole('button', { name: /^Rack: ASA, Spool #127/ });
      expect(rack).toHaveAttribute('aria-pressed', 'false');
      expect(screen.queryByText('In use')).not.toBeInTheDocument();
      expect(screen.queryByText(/Feeding from/)).not.toBeInTheDocument();
    });

    it('hides the Rack when the device reports no bypass holder', () => {
      render(
        <MmuControlBox
          {...props}
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, activeGate: -2, activeTool: -2, hasBypass: false })}
        />,
      );

      expect(screen.queryByRole('button', { name: /^Rack:/ })).not.toBeInTheDocument();
      expect(screen.queryByText('In use')).not.toBeInTheDocument();
    });

    it('does not surface a Rack for non-QidiBox units or ambiguous physical toolheads', () => {
      const { rerender } = render(
        <MmuControlBox
          {...props}
          toolheads={rackToolheads}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.HappyHare, activeGate: -1 })}
        />,
      );
      expect(screen.queryByRole('button', { name: /^Rack:/ })).not.toBeInTheDocument();

      rerender(
        <MmuControlBox
          {...props}
          toolheads={[...rackToolheads, toolhead(4, 'Physical')]}
          mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox, hasBypass: true, activeGate: -1 })}
        />,
      );
      expect(screen.queryByRole('button', { name: /^Rack:/ })).not.toBeInTheDocument();
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
  const loaded = screen.getByRole('button', { name: /^Gate 1A:/ });
  const depleted = screen.getByRole('button', { name: /^Gate 1B:/ });
  expect(depleted).toHaveTextContent('Assigned spool depleted');
  fireEvent.click(depleted);
  expect(depleted).toHaveAttribute('aria-pressed', 'true');
  expect(depleted).not.toHaveTextContent('In use');
  expect(loaded).toHaveTextContent('In use');
  expect(loaded).toHaveAttribute('aria-current', 'true');
  expect(screen.getByRole('button', { name: /^Gate 1C:/ })).toHaveTextContent('Filament not loaded');
  expect(screen.getAllByText('In use')).toHaveLength(1);
});


it.each(['start', 'write'])('respects the separate queue:%s permission', (action) => {
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  permissions.operate = action !== 'start';
  permissions.assign = action !== 'write';
  inventory.data = undefined;
  inventory.isError = false;
  render(<MmuControlBox printerId="printer-1" isOnline reviewedRowVersion="rev-1" toolheads={threePersistedGates}
    mmuStatus={status(qidiGates, { mmuType: MmuProtocol.Qidibox })} />);
  if (action === 'start') {
    expect(screen.getByRole('button', { name: 'Load' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Assign' })).not.toHaveAttribute('aria-disabled', 'true');
  } else {
    expect(screen.getByRole('button', { name: 'Assign' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Load' })).toBeEnabled();
  }
});


it('preserves recovery during an apparently pending MMU action', async () => {
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  permissions.assign = permissions.operate = true;
  let complete: () => void = () => {};
  vi.mocked(apiClient.mmuEject).mockImplementation(() => new Promise((resolve) => { complete = () => resolve({ success: true, message: 'Fixture complete' }); }));
  render(<MmuControlBox printerId="printer-1" isOnline reviewedRowVersion="rev-1" toolheads={threePersistedGates}
    mmuStatus={status(qidiGates, { mmuType: MmuProtocol.HappyHare, action: 'Error' })} />);
  fireEvent.click(screen.getByRole('button', { name: 'Unload' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Load' })).toBeDisabled());
  expect(screen.getByRole('button', { name: 'Recover' })).toBeEnabled();
  complete();
  await waitFor(() => expect(screen.getByRole('button', { name: 'Load' })).toBeEnabled());
});
