import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

const mockUseReorder = vi.fn();
vi.mock('../hooks/usePartsInventory', () => ({
  useReorderCandidates: (...args: unknown[]) => mockUseReorder(...args),
}));

import { ReorderTab } from '../components/ReorderTab';

describe('ReorderTab', () => {
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
