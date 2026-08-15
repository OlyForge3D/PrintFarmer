import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ToolheadSpoolPicker } from '../ToolheadSpoolPicker';
import type { SpoolmanSpool, ToolheadDto } from '@/types/api';

const mocks = vi.hoisted(() => ({
  setSpoolMutateAsync: vi.fn(),
  clearSpoolMutate: vi.fn(),
}));

vi.mock('@/common/hooks/useApi', () => ({
  useSetToolheadSpool: () => ({
    mutateAsync: mocks.setSpoolMutateAsync,
    isPending: false,
  }),
  useClearToolheadSpool: () => ({
    mutate: mocks.clearSpoolMutate,
    isPending: false,
  }),
}));

vi.mock('@/features/printers/components/SpoolPickerModal', () => ({
  SpoolPickerModal: ({ onSelect }: { onSelect: (spoolId: number, spool: SpoolmanSpool) => void }) => (
    <>
      <button type="button" onClick={() => onSelect(1, { id: 1, name: 'Manual', material: 'PLA', inUse: false })}>
        Pick manual spool
      </button>
      {/* The real picker's Eject action reports spool id 0. */}
      <button type="button" data-testid="spool-picker-eject" onClick={() => onSelect(0, {} as SpoolmanSpool)}>
        Eject
      </button>
    </>
  ),
}));

const toolheads: ToolheadDto[] = [
  {
    id: 'tool-0',
    index: 0,
    isPrimary: true,
    currentSpoolId: 1,
    currentMaterial: 'PLA',
    currentFilamentColor: '#FF0000',
  },
  {
    id: 'tool-1',
    index: 1,
    isPrimary: false,
    currentSpoolId: 2,
    currentMaterial: 'PETG',
    currentFilamentColor: '#0000FF',
  },
];

describe('ToolheadSpoolPicker', () => {
  beforeEach(() => {
    mocks.setSpoolMutateAsync.mockResolvedValue('printer-v2');
    mocks.setSpoolMutateAsync.mockClear();
    mocks.clearSpoolMutate.mockClear();
  });

  it('shows suggestions with confidence and material mismatch text', () => {
    render(
      <ToolheadSpoolPicker
        printerId="printer-1"
        reviewedRowVersion="printer-v1"
        toolheads={toolheads}
        targetFilamentColorHex={['#0000FF', '#FF0000']}
        targetFilamentType={['PETG', 'ABS']}
      />,
    );

    expect(screen.getByText('Suggested spool #2')).toBeInTheDocument();
    expect(screen.getByText('Suggested spool #1')).toBeInTheDocument();
    expect(screen.getByText('Exact color')).toBeInTheDocument();
    expect(screen.getByText('Material mismatch')).toBeInTheDocument();
    expect(screen.getByText('Expected ABS, loaded PLA')).toBeInTheDocument();
  });

  it('does not auto-match over a manual override', async () => {
    render(
      <ToolheadSpoolPicker
        printerId="printer-1"
        reviewedRowVersion="printer-v1"
        toolheads={toolheads}
        targetFilamentColorHex={['#0000FF', '#FF0000']}
        targetFilamentType={['PLA', 'PLA']}
      />,
    );

    fireEvent.click(screen.getAllByText('Change')[0]);
    fireEvent.click(screen.getByText('Pick manual spool'));
    await waitFor(() => expect(mocks.setSpoolMutateAsync).toHaveBeenCalledWith({
      printerId: 'printer-1',
      toolheadIndex: 0,
      spoolId: 1,
      reviewedRowVersion: 'printer-v1',
    }));
    await waitFor(() => expect(screen.queryByText('Suggested spool #1')).not.toBeInTheDocument());
    expect(screen.getByText('Suggested spool #2')).toBeInTheDocument();
    expect(screen.getByText('Manual override')).toBeInTheDocument();

    mocks.setSpoolMutateAsync.mockClear();
    fireEvent.click(screen.getByText('Auto-match all'));

    expect(mocks.setSpoolMutateAsync).not.toHaveBeenCalled();
    expect(mocks.setSpoolMutateAsync).not.toHaveBeenCalledWith({
      printerId: 'printer-1',
      toolheadIndex: 1,
      spoolId: 1,
    });
  });

  it('routes the picker eject action to the clear endpoint, not a spool-0 bind', async () => {
    // SpoolPickerModal's Eject button reports spool id 0. Forwarding that to
    // setSpool would persist a bogus zero binding instead of releasing the slot.
    render(
      <ToolheadSpoolPicker
        printerId="printer-1"
        reviewedRowVersion="printer-v1"
        toolheads={toolheads}
      />,
    );

    fireEvent.click(screen.getAllByText('Change')[0]);
    fireEvent.click(await screen.findByTestId('spool-picker-eject'));

    await waitFor(() =>
      expect(mocks.clearSpoolMutate).toHaveBeenCalledWith({
        printerId: 'printer-1',
        toolheadIndex: 0,
        reviewedRowVersion: 'printer-v1',
      }),
    );
    expect(mocks.setSpoolMutateAsync).not.toHaveBeenCalled();
  });
});
