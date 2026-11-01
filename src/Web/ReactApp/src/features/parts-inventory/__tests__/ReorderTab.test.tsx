import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';

const mockUseReorder = vi.fn();
vi.mock('../hooks/usePartsInventory', () => ({
  useReorderCandidates: (...args: unknown[]) => mockUseReorder(...args),
}));

import { ReorderTab } from '../components/ReorderTab';

describe('ReorderTab', () => {
  it('sorts numeric deficits through the full keyboard cycle without changing the incoming priority order', async () => {
    mockUseReorder.mockReturnValue({
      data: [
        { partInventoryId: 'p1', sku: 'HIGH', name: 'High', onHand: 0, reorderPoint: 10, deficit: 10 },
        { partInventoryId: 'p2', sku: 'LOW', name: 'Low', onHand: 1, reorderPoint: 3, deficit: 2 },
      ],
      isLoading: false, error: null, refetch: vi.fn(), isFetching: false,
    });
    const user = userEvent.setup();
    render(<ReorderTab />);
    const rows = () => screen.getAllByRole('row').slice(1).map(row => row.firstElementChild?.textContent);
    const button = screen.getByRole('button', { name: 'Deficit' });
    expect(screen.getAllByRole('columnheader').every(header => header.getAttribute('aria-sort') === 'none')).toBe(true);
    expect(rows()).toEqual(['HIGH', 'LOW']);
    await user.click(button);
    expect(rows()).toEqual(['LOW', 'HIGH']);
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'ascending');
    await user.keyboard('{Enter}');
    expect(rows()).toEqual(['HIGH', 'LOW']);
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'descending');
    await user.keyboard(' ');
    expect(rows()).toEqual(['HIGH', 'LOW']);
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'none');
  });

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders empty state when no candidates', () => {
    mockUseReorder.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
      refetch: vi.fn(),
      isFetching: false,
    });
    render(<ReorderTab />);
    expect(screen.getByText('All SKUs above reorder point')).toBeInTheDocument();
  });

  it('renders "Below reorder pt" badge with icon+text when onHand > 0', () => {
    mockUseReorder.mockReturnValue({
      data: [
        {
          partInventoryId: 'p1',
          sku: 'BRK-1',
          name: 'Bracket',
          onHand: 3,
          reorderPoint: 10,
          deficit: 7,
        },
      ],
      isLoading: false,
      error: null,
      refetch: vi.fn(),
      isFetching: false,
    });
    render(<ReorderTab />);
    expect(screen.getByText('Below reorder pt')).toBeInTheDocument();
    expect(screen.getByText('BRK-1')).toBeInTheDocument();
    expect(screen.getByText('7')).toBeInTheDocument();
  });

  it('renders "Out of stock" badge when onHand <= 0', () => {
    mockUseReorder.mockReturnValue({
      data: [
        {
          partInventoryId: 'p2',
          sku: 'OUT-1',
          name: 'Out',
          onHand: 0,
          reorderPoint: 5,
          deficit: 5,
        },
      ],
      isLoading: false,
      error: null,
      refetch: vi.fn(),
      isFetching: false,
    });
    render(<ReorderTab />);
    expect(screen.getByText('Out of stock')).toBeInTheDocument();
  });

  it('shows loading state', () => {
    mockUseReorder.mockReturnValue({
      data: undefined,
      isLoading: true,
      error: null,
      refetch: vi.fn(),
      isFetching: true,
    });
    render(<ReorderTab />);
    expect(screen.getByText(/Loading reorder candidates/i)).toBeInTheDocument();
  });

  it('shows error state', () => {
    mockUseReorder.mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error('nope'),
      refetch: vi.fn(),
      isFetching: false,
    });
    render(<ReorderTab />);
    expect(screen.getByRole('alert')).toHaveTextContent(/Failed to load/i);
  });
});
