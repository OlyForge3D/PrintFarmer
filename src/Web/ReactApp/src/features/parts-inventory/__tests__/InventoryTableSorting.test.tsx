import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { BinDto, PartInventoryDto } from '@/types/partsInventory';

const { binsQuery, partsQuery } = vi.hoisted(() => ({
  binsQuery: vi.fn(),
  partsQuery: vi.fn(),
}));
vi.mock('../hooks/usePartsInventory', () => ({
  useBins: binsQuery,
  useParts: partsQuery,
  useDeleteBin: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useDeletePart: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));
vi.mock('../components/BinFormModal', () => ({ BinFormModal: () => null }));
vi.mock('../components/RegisterBinModal', () => ({ RegisterBinModal: () => null }));
vi.mock('../components/PartFormModal', () => ({ PartFormModal: () => null }));
vi.mock('../components/AdjustStockModal', () => ({ AdjustStockModal: () => null }));
vi.mock('../components/AdjustmentHistoryDrawer', () => ({ AdjustmentHistoryDrawer: () => null }));

import { BinsTab } from '../components/BinsTab';
import { PartsTab } from '../components/PartsTab';

const bin: BinDto = {
  id: 'bin-a', code: 'A', name: 'Alpha', location: null, notes: null,
  isActive: true, createdAt: '', updatedAt: '',
};
const part: PartInventoryDto = {
  id: 'part-a', sku: 'A', name: 'Alpha', description: null, modelFileRef: null,
  defaultBinId: null, defaultBinCode: null, defaultBinName: null, onHand: 10,
  reorderPoint: 2, needsReorder: false, isActive: true, createdAt: '', updatedAt: '',
};
const rowCodes = () => screen.getAllByRole('row').slice(1).map(row => row.firstElementChild?.textContent);

describe('Inventory table sorting', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    binsQuery.mockReturnValue({ data: [], isLoading: false, error: null });
    partsQuery.mockReturnValue({ data: [], isLoading: false, error: null });
  });

  it('keeps absent bin locations last in both directions and restores incoming rows', async () => {
    binsQuery.mockReturnValue({
      data: [
        bin,
        { ...bin, id: 'bin-z', code: 'Z', name: 'Zulu', location: 'West' },
        { ...bin, id: 'bin-b', code: 'B', name: 'Beta', location: 'East' },
      ], isLoading: false, error: null,
    });
    const user = userEvent.setup();
    render(<BinsTab />);
    expect(screen.getByRole('columnheader', { name: 'Actions' })).not.toHaveAttribute('aria-sort');
    const button = screen.getByRole('button', { name: 'Location' });
    await user.click(button);
    expect(rowCodes()).toEqual(['B', 'Z', 'A']);
    await user.keyboard('{Enter}');
    expect(rowCodes()).toEqual(['Z', 'B', 'A']);
    await user.keyboard(' ');
    expect(rowCodes()).toEqual(['A', 'Z', 'B']);
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'none');
    await user.click(screen.getByRole('button', { name: 'Name' }));
    await user.type(screen.getByRole('searchbox', { name: 'Search bins' }), 'Zulu');
    expect(rowCodes()).toEqual(['Z']);
    expect(within(screen.getAllByRole('row')[1]).getByRole('button', { name: 'Edit bin Z' })).toBeInTheDocument();
  });

  it('sorts stock numerically, optional bins last, and status by the displayed labels', async () => {
    partsQuery.mockReturnValue({
      data: [
        part,
        { ...part, id: 'part-b', sku: 'B', name: 'Beta', onHand: 2, defaultBinCode: 'BIN-Z', needsReorder: true },
        { ...part, id: 'part-c', sku: 'C', name: 'Charlie', onHand: -1, defaultBinCode: 'BIN-A', isActive: false },
      ], isLoading: false, error: null,
    });
    const user = userEvent.setup();
    render(<PartsTab />);
    const button = screen.getByRole('button', { name: 'On-hand' });
    await user.click(button);
    expect(rowCodes()).toEqual(['C', 'B', 'A']);
    await user.keyboard('{Enter}');
    expect(rowCodes()).toEqual(['A', 'B', 'C']);
    await user.keyboard(' ');
    expect(rowCodes()).toEqual(['A', 'B', 'C']);
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'none');
    await user.click(screen.getByRole('button', { name: 'Default bin' }));
    expect(rowCodes()).toEqual(['C', 'B', 'A']);
    await user.keyboard('{Enter}');
    expect(rowCodes()).toEqual(['B', 'C', 'A']);
    await user.click(screen.getByRole('button', { name: 'Status' }));
    expect(rowCodes()).toEqual(['A', 'C', 'B']);
    expect(within(screen.getAllByRole('row')[3]).getByRole('button', { name: 'Adjust stock for B' })).toBeInTheDocument();
  });
});
