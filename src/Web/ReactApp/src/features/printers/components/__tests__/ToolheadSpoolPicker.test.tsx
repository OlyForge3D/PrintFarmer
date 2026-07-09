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
    <button type="button" onClick={() => onSelect(99, { id: 99, name: 'Manual', material: 'PLA', inUse: false })}>
      Pick manual spool
    </button>
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
    mocks.setSpoolMutateAsync.mockResolvedValue(undefined);
    mocks.setSpoolMutateAsync.mockClear();
    mocks.clearSpoolMutate.mockClear();
  });

  it('shows suggestions with confidence and material mismatch text', () => {
    render(
      <ToolheadSpoolPicker
        printerId="printer-1"
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
      spoolId: 99,
    }));

    fireEvent.click(screen.getByText('Auto-match all'));

    await waitFor(() => expect(mocks.setSpoolMutateAsync).toHaveBeenCalledWith({
      printerId: 'printer-1',
      toolheadIndex: 1,
      spoolId: 1,
    }));
    expect(mocks.setSpoolMutateAsync).not.toHaveBeenCalledWith({
      printerId: 'printer-1',
      toolheadIndex: 0,
      spoolId: 2,
    });
  });
});
