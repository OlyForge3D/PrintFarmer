import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MmuControlBox } from '../MmuControlBox';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';
import { MmuGateStatus, type MmuGate, type MmuStatus, type ToolheadDto } from '@/types/api';

const setSpool = vi.fn();
const clearSpool = vi.fn();

vi.mock('@/common/hooks/useApi', () => ({
  useSetToolheadSpool: () => ({ mutateAsync: setSpool, isPending: false }),
  useClearToolheadSpool: () => ({ mutateAsync: clearSpool, isPending: false }),
  usePrinterDetails: () => ({ data: undefined }),
}));

vi.mock('@/services/api', () => ({ apiClient: {} }));

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
    setSpool.mockReset().mockResolvedValue('rev-2');
    clearSpool.mockReset().mockResolvedValue('rev-2');
  });

  it('uses the live inset-surface token for every spool hub', () => {
    const { container } = render(
      <MmuControlBox printerId="printer-1" mmuStatus={status([gate(0, { spoolId: 1 })])} isOnline />,
    );

    const hubs = container.querySelectorAll('ellipse[cx="28"][cy="30"]');
    expect(hubs.length).toBeGreaterThan(0);
    for (const hub of hubs) {
      expect(hub).toHaveAttribute('fill', 'var(--pf-bg-2)');
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
});
