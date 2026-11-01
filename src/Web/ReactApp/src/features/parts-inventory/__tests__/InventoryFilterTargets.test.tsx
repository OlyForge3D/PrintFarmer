import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PartsTab } from '../components/PartsTab';
import { BinsTab } from '../components/BinsTab';

const { useParts, useBins } = vi.hoisted(() => ({
  useParts: vi.fn(() => ({ data: [], isLoading: false })),
  useBins: vi.fn(() => ({ data: [], isLoading: false })),
}));

vi.mock('../hooks/usePartsInventory', () => ({
  useParts,
  useBins,
  useDeletePart: () => ({}),
  useDeleteBin: () => ({}),
}));
vi.mock('../components/PartFormModal', () => ({ PartFormModal: () => null }));
vi.mock('../components/BinFormModal', () => ({ BinFormModal: () => null }));
vi.mock('../components/RegisterBinModal', () => ({ RegisterBinModal: () => null }));
vi.mock('../components/AdjustStockModal', () => ({ AdjustStockModal: () => null }));
vi.mock('../components/AdjustmentHistoryDrawer', () => ({ AdjustmentHistoryDrawer: () => null }));

describe('Inventory inactive filter hit targets', () => {
  beforeEach(() => vi.clearAllMocks());

  it.each([
    { name: 'SKUs', Component: PartsTab, query: useParts, target: 'inactive-skus' },
    { name: 'Bins', Component: BinsTab, query: useBins, target: 'inactive-bins' },
  ])('keeps $name filters in the shared disclosure and reset independent of search', ({ name, Component, query, target }) => {
    const { container } = render(<Component />);
    const search = screen.getByRole('searchbox', { name: name === 'SKUs' ? 'Search SKUs' : 'Search bins' });
    fireEvent.change(search, { target: { value: 'sample search' } });

    const filterButton = screen.getByRole('button', { name: 'Filters' });
    expect(filterButton).toHaveAttribute('aria-expanded', 'false');
    fireEvent.click(filterButton);

    const checkbox = screen.getByRole('checkbox', { name: 'Show inactive' });
    const hitTarget = container.querySelector(`[data-filter-hit-target="${target}"]`);
    const switchLabel = checkbox.closest('label');
    const textLabel = screen.getByText('Show inactive');

    expect(hitTarget).toContainElement(switchLabel);
    expect(hitTarget).toContainElement(textLabel);
    expect(checkbox).not.toBeChecked();
    expect(query).toHaveBeenLastCalledWith({ includeInactive: false });

    fireEvent.click(textLabel);
    expect(checkbox).toBeChecked();
    expect(query).toHaveBeenLastCalledWith({ includeInactive: true });

    fireEvent.click(switchLabel!);
    expect(checkbox).not.toBeChecked();
    expect(query).toHaveBeenLastCalledWith({ includeInactive: false });

    fireEvent.click(textLabel);
    expect(screen.getByRole('button', { name: 'Filters (1)' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Reset filters' }));
    expect(checkbox).not.toBeChecked();
    expect(query).toHaveBeenLastCalledWith({ includeInactive: false });
    expect(search).toHaveValue('sample search');
  });
});
