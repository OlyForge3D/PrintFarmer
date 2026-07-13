import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

const mockMutateAsync = vi.fn();

vi.mock('../hooks/usePartsInventory', () => ({
  useAdjustPartStock: () => ({ mutateAsync: mockMutateAsync, isPending: false }),
}));

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

import { AdjustStockModal } from '../components/AdjustStockModal';
import type { BinDto, PartInventoryDto } from '@/types/partsInventory';

function makePart(overrides: Partial<PartInventoryDto> = {}): PartInventoryDto {
  return {
    id: 'p1',
    sku: 'BRK-1',
    name: 'Bracket',
    description: null,
    modelFileRef: null,
    defaultBinId: 'b1',
    defaultBinCode: 'A1',
    defaultBinName: 'Rack A',
    onHand: 10,
    reorderPoint: 2,
    needsReorder: false,
    isActive: true,
    createdAt: '2024-01-01T00:00:00Z',
    updatedAt: '2024-01-01T00:00:00Z',
    ...overrides,
  };
}

const bins: BinDto[] = [
  {
    id: 'b1',
    code: 'A1',
    name: 'Rack A',
    location: null,
    notes: null,
    isActive: true,
    createdAt: '2024-01-01T00:00:00Z',
    updatedAt: '2024-01-01T00:00:00Z',
  },
];

function submit() {
  fireEvent.click(screen.getByRole('button', { name: /record adjustment/i }));
}

describe('AdjustStockModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('reuses the SAME operationKey when an identical payload is retried after a failure', async () => {
    // First submit fails with a transient (network) error — no HTTP status —
    // then the identical payload is retried. The server must see the same
    // operationKey so it dedupes rather than applying the delta twice.
    mockMutateAsync
      .mockRejectedValueOnce({ message: 'Network Error' })
      .mockResolvedValueOnce({});

    render(<AdjustStockModal isOpen onClose={vi.fn()} part={makePart()} bins={bins} />);

    submit();
    await waitFor(() => expect(mockMutateAsync).toHaveBeenCalledTimes(1));

    submit();
    await waitFor(() => expect(mockMutateAsync).toHaveBeenCalledTimes(2));

    const firstKey = mockMutateAsync.mock.calls[0][0].request.operationKey;
    const secondKey = mockMutateAsync.mock.calls[1][0].request.operationKey;
    expect(firstKey).toBeTruthy();
    expect(secondKey).toBe(firstKey);
  });

  it('rotates the operationKey after a 409 rejection (new logical operation)', async () => {
    mockMutateAsync
      .mockRejectedValueOnce({ statusCode: 409, message: 'conflict' })
      .mockResolvedValueOnce({});

    render(<AdjustStockModal isOpen onClose={vi.fn()} part={makePart()} bins={bins} />);

    submit();
    await waitFor(() => expect(mockMutateAsync).toHaveBeenCalledTimes(1));

    submit();
    await waitFor(() => expect(mockMutateAsync).toHaveBeenCalledTimes(2));

    const firstKey = mockMutateAsync.mock.calls[0][0].request.operationKey;
    const secondKey = mockMutateAsync.mock.calls[1][0].request.operationKey;
    expect(secondKey).not.toBe(firstKey);
  });

  it('substitutes the SKU default bin when "Use default" is chosen', async () => {
    mockMutateAsync.mockResolvedValueOnce({});
    render(<AdjustStockModal isOpen onClose={vi.fn()} part={makePart({ defaultBinCode: 'A1' })} bins={bins} />);

    // Select the "— Use default —" option (empty value) explicitly.
    fireEvent.change(screen.getByLabelText('Bin'), { target: { value: '' } });
    expect(screen.getByRole('option', { name: '— Use default —' })).toBeInTheDocument();

    submit();
    await waitFor(() => expect(mockMutateAsync).toHaveBeenCalledTimes(1));
    expect(mockMutateAsync.mock.calls[0][0].request.binCode).toBe('A1');
  });

  it('sends binCode=null and labels the option "— No bin —" when the SKU has no default', async () => {
    mockMutateAsync.mockResolvedValueOnce({});
    render(
      <AdjustStockModal
        isOpen
        onClose={vi.fn()}
        part={makePart({ defaultBinCode: null, defaultBinName: null, defaultBinId: null })}
        bins={bins}
      />
    );

    expect(screen.getByRole('option', { name: '— No bin —' })).toBeInTheDocument();

    submit();
    await waitFor(() => expect(mockMutateAsync).toHaveBeenCalledTimes(1));
    expect(mockMutateAsync.mock.calls[0][0].request.binCode).toBeNull();
  });
});
