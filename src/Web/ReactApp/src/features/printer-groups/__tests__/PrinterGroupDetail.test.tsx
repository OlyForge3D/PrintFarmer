import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { PrinterGroupDetail as PrinterGroupDetailType } from '@/types/api';

const { mockGetPrinterGroup } = vi.hoisted(() => ({
  mockGetPrinterGroup: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterGroup: (...args: unknown[]) => mockGetPrinterGroup(...args),
  },
}));

vi.mock('@/common/components/ui', () => ({
  Card: Object.assign(
    ({ children }: { children: React.ReactNode }) => <div data-testid="card">{children}</div>,
    {
      Header: ({ children }: { children: React.ReactNode }) => <div data-testid="card-header">{children}</div>,
      Body: ({ children }: { children: React.ReactNode }) => <div data-testid="card-body">{children}</div>,
      Footer: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
    },
  ),
  Spinner: ({ size }: { size?: string }) => <div data-testid="spinner" data-size={size}>Loading...</div>,
  Button: ({ children, onClick, ...rest }: React.ButtonHTMLAttributes<HTMLButtonElement> & { variant?: string; iconLeft?: React.ReactNode }) => (
    <button onClick={onClick} {...rest}>{rest.iconLeft}{children}</button>
  ),
  Badge: ({ children }: { children: React.ReactNode; variant?: string }) => (
    <span data-testid="badge">{children}</span>
  ),
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  ArrowLeftIcon: () => <span data-testid="arrow-left-icon" />,
  EditIcon: () => <span data-testid="edit-icon" />,
  DeleteIcon: () => <span data-testid="delete-icon" />,
}));

vi.mock('date-fns', () => ({
  formatDistanceToNow: () => '3 days ago',
}));

vi.mock('../components/PrinterAssignment', () => ({
  PrinterAssignment: ({ groupId }: { groupId: string; assignedPrinters: unknown[] }) => (
    <div data-testid="printer-assignment" data-group-id={groupId}>Assignment Section</div>
  ),
}));

import { PrinterGroupDetail } from '../components/PrinterGroupDetail';

const createQueryClient = () =>
  new QueryClient({ defaultOptions: { queries: { retry: false } } });

const mockGroupDetail: PrinterGroupDetailType = {
  id: 'g1',
  name: 'Test Group',
  description: 'A test group',
  createdDate: '2025-01-01T00:00:00Z',
  updatedDate: '2025-01-02T00:00:00Z',
  printers: [
    { id: 'p1', name: 'Printer 1', backend: 1, isAvailable: true, inMaintenance: false },
  ],
};

describe('PrinterGroupDetail', () => {
  const onBack = vi.fn();
  const onEdit = vi.fn();
  const onDelete = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    mockGetPrinterGroup.mockReset();
  });

  const renderDetail = (groupId = 'g1') =>
    render(
      <QueryClientProvider client={createQueryClient()}>
        <PrinterGroupDetail groupId={groupId} onBack={onBack} onEdit={onEdit} onDelete={onDelete} />
      </QueryClientProvider>,
    );

  it('shows spinner while loading', () => {
    mockGetPrinterGroup.mockReturnValue(new Promise(() => {}));
    renderDetail();
    expect(screen.getByTestId('spinner')).toBeInTheDocument();
  });

  it('shows error state when fetch fails', async () => {
    mockGetPrinterGroup.mockRejectedValue(new Error('Not found'));
    renderDetail();
    await waitFor(() => {
      expect(screen.getByText(/Failed to load group details/)).toBeInTheDocument();
    });
  });

  it('shows Back to Groups button on error', async () => {
    mockGetPrinterGroup.mockRejectedValue(new Error('Not found'));
    renderDetail();
    await waitFor(() => {
      expect(screen.getByText('Back to Groups')).toBeInTheDocument();
    });
  });

  it('renders group name and description on success', async () => {
    mockGetPrinterGroup.mockResolvedValue(mockGroupDetail);
    renderDetail();
    await waitFor(() => {
      expect(screen.getByText('Test Group')).toBeInTheDocument();
      expect(screen.getByText('A test group')).toBeInTheDocument();
    });
  });

  it('renders printer count badge', async () => {
    mockGetPrinterGroup.mockResolvedValue(mockGroupDetail);
    renderDetail();
    await waitFor(() => {
      expect(screen.getByText('1 printer')).toBeInTheDocument();
    });
  });

  it('renders plural printers count', async () => {
    mockGetPrinterGroup.mockResolvedValue({
      ...mockGroupDetail,
      printers: [
        { id: 'p1', name: 'P1', backend: 1, isAvailable: true, inMaintenance: false },
        { id: 'p2', name: 'P2', backend: 1, isAvailable: true, inMaintenance: false },
      ],
    });
    renderDetail();
    await waitFor(() => {
      expect(screen.getByText('2 printers')).toBeInTheDocument();
    });
  });

  it('renders timestamp info', async () => {
    mockGetPrinterGroup.mockResolvedValue(mockGroupDetail);
    renderDetail();
    await waitFor(() => {
      expect(screen.getByText(/Created 3 days ago/)).toBeInTheDocument();
      expect(screen.getByText(/Updated 3 days ago/)).toBeInTheDocument();
    });
  });

  it('renders PrinterAssignment component', async () => {
    mockGetPrinterGroup.mockResolvedValue(mockGroupDetail);
    renderDetail();
    await waitFor(() => {
      expect(screen.getByTestId('printer-assignment')).toBeInTheDocument();
      expect(screen.getByTestId('printer-assignment')).toHaveAttribute('data-group-id', 'g1');
    });
  });

  it('calls onBack when back button is clicked', async () => {
    mockGetPrinterGroup.mockResolvedValue(mockGroupDetail);
    renderDetail();
    await waitFor(() => screen.getByText('Test Group'));
    fireEvent.click(screen.getByText('Back to Groups'));
    expect(onBack).toHaveBeenCalled();
  });

  it('calls onEdit when edit button is clicked', async () => {
    mockGetPrinterGroup.mockResolvedValue(mockGroupDetail);
    renderDetail();
    await waitFor(() => screen.getByText('Edit'));
    fireEvent.click(screen.getByText('Edit'));
    expect(onEdit).toHaveBeenCalledWith(expect.objectContaining({ id: 'g1', name: 'Test Group' }));
  });

  it('calls onDelete when delete button is clicked', async () => {
    mockGetPrinterGroup.mockResolvedValue(mockGroupDetail);
    renderDetail();
    await waitFor(() => screen.getByText('Delete'));
    fireEvent.click(screen.getByText('Delete'));
    expect(onDelete).toHaveBeenCalledWith(expect.objectContaining({ id: 'g1', name: 'Test Group' }));
  });

  it('does not render description when not provided', async () => {
    mockGetPrinterGroup.mockResolvedValue({ ...mockGroupDetail, description: undefined });
    renderDetail();
    await waitFor(() => screen.getByText('Test Group'));
    expect(screen.queryByText('A test group')).not.toBeInTheDocument();
  });
});
