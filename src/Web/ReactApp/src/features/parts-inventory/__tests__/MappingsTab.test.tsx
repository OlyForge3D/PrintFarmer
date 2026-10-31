import { render, screen, within } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';

const mockUseMappings = vi.fn();
const mockUseParts = vi.fn();
const mockUseDeleteMapping = vi.fn();

vi.mock('../hooks/usePartsInventory', () => ({
  useMappings: (...args: unknown[]) => mockUseMappings(...args),
  useParts: (...args: unknown[]) => mockUseParts(...args),
  useDeleteMapping: () => mockUseDeleteMapping(),
}));

vi.mock('../components/MappingFormModal', () => ({
  MappingFormModal: () => <div data-testid="mapping-form-modal" />,
}));

import { MappingsTab } from '../components/MappingsTab';

describe('MappingsTab', () => {
  it('sorts quantities within each source independently and keeps removal tied to the displayed mapping', async () => {
    mockUseMappings.mockReturnValue({
      data: [
        { id: 'm1', sku: 'HIGH', quantity: 10, gcodeFileId: 'g1', printProjectFileId: null },
        { id: 'm2', sku: 'LOW', quantity: 2, gcodeFileId: 'g1', printProjectFileId: null },
        { id: 'm3', sku: 'OTHER', quantity: 4, gcodeFileId: 'g2', printProjectFileId: null },
      ],
      isLoading: false, error: null,
    });
    const user = userEvent.setup();
    render(<MappingsTab />);
    const [first, second] = screen.getAllByRole('table');
    const rows = () => within(first).getAllByRole('row').slice(1).map(row => row.firstElementChild?.textContent);
    const button = within(first).getByRole('button', { name: 'Quantity' });
    await user.click(button);
    expect(rows()).toEqual(['LOW', 'HIGH']);
    expect(within(second).getByRole('button', { name: 'Quantity' }).closest('th')).toHaveAttribute('aria-sort', 'none');
    await user.keyboard('{Enter}');
    expect(rows()).toEqual(['HIGH', 'LOW']);
    await user.keyboard(' ');
    expect(rows()).toEqual(['HIGH', 'LOW']);
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'none');
    await user.click(within(first).getByRole('button', { name: 'Remove mapping for LOW' }));
    expect(screen.getByRole('dialog')).toHaveTextContent('to LOW');
  });

  beforeEach(() => {
    vi.clearAllMocks();
    mockUseParts.mockReturnValue({ data: [] });
    mockUseDeleteMapping.mockReturnValue({ mutateAsync: vi.fn(), isPending: false });
  });

  it('renders empty state when no mappings', () => {
    mockUseMappings.mockReturnValue({ data: [], isLoading: false, error: null });
    render(<MappingsTab />);
    expect(screen.getByText('No output mappings')).toBeInTheDocument();
  });

  it('groups multi-SKU plates and marks them with a Multi-SKU badge', () => {
    mockUseMappings.mockReturnValue({
      data: [
        {
          id: 'm1',
          sku: 'BRK-1',
          quantity: 1,
          gcodeFileId: 'gcode-1',
          printProjectFileId: null,
        },
        {
          id: 'm2',
          sku: 'BRK-2',
          quantity: 2,
          gcodeFileId: 'gcode-1',
          printProjectFileId: null,
        },
        {
          id: 'm3',
          sku: 'SOLO',
          quantity: 1,
          gcodeFileId: null,
          printProjectFileId: 'project-1',
        },
      ],
      isLoading: false,
      error: null,
    });

    render(<MappingsTab />);

    expect(screen.getByText('Multi-SKU plate (2)')).toBeInTheDocument();
    expect(screen.getByText('BRK-1')).toBeInTheDocument();
    expect(screen.getByText('BRK-2')).toBeInTheDocument();
    expect(screen.getByText('SOLO')).toBeInTheDocument();
    expect(screen.getByText('gcode-1')).toBeInTheDocument();
    expect(screen.getByText('project-1')).toBeInTheDocument();
  });

  it('does not show Multi-SKU badge for single-mapping group', () => {
    mockUseMappings.mockReturnValue({
      data: [
        {
          id: 'm1',
          sku: 'S',
          quantity: 1,
          gcodeFileId: 'g1',
          printProjectFileId: null,
        },
      ],
      isLoading: false,
      error: null,
    });
    render(<MappingsTab />);
    expect(screen.queryByText(/Multi-SKU plate/i)).not.toBeInTheDocument();
  });

  it('shows error state on load failure', () => {
    mockUseMappings.mockReturnValue({ data: undefined, isLoading: false, error: new Error('x') });
    render(<MappingsTab />);
    expect(screen.getByRole('alert')).toHaveTextContent(/Failed to load/i);
  });
});
