import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { PrinterGroup } from '@/types/api';

const { mockToast, mockGetPrinterGroups, mockDeletePrinterGroup } = vi.hoisted(() => ({
  mockToast: { success: vi.fn(), error: vi.fn() },
  mockGetPrinterGroups: vi.fn(),
  mockDeletePrinterGroup: vi.fn(),
}));

vi.mock('sonner', () => ({ toast: mockToast }));

const mockGroups: PrinterGroup[] = [
  {
    id: 'g1',
    name: 'Group Alpha',
    description: 'First group',
    createdDate: '2025-01-01T00:00:00Z',
    updatedDate: '2025-01-02T00:00:00Z',
    printerCount: 2,
  },
  {
    id: 'g2',
    name: 'Group Beta',
    description: 'Second group',
    createdDate: '2025-01-03T00:00:00Z',
    updatedDate: '2025-01-04T00:00:00Z',
    printerCount: 5,
  },
];

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterGroups: (...args: unknown[]) => mockGetPrinterGroups(...args),
    deletePrinterGroup: (...args: unknown[]) => mockDeletePrinterGroup(...args),
  },
}));

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ children, title, actions }: { children: React.ReactNode; title: string; subtitle?: string; icon?: React.ReactNode; actions?: React.ReactNode }) => (
    <div data-testid="page-template" data-title={title}>{actions}{children}</div>
  ),
}));

vi.mock('@/common/components/ui', () => ({
  Spinner: ({ size }: { size?: string }) => <div data-testid="spinner" data-size={size}>Loading...</div>,
  Button: ({ children, onClick, ...rest }: React.ButtonHTMLAttributes<HTMLButtonElement> & { variant?: string; iconLeft?: React.ReactNode }) => (
    <button onClick={onClick} {...rest}>{rest.iconLeft}{children}</button>
  ),
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  PlusIcon: () => <span data-testid="plus-icon" />,
  PrinterIcon: ({ className }: { className?: string }) => <span data-testid="printer-icon" className={className} />,
}));

vi.mock('../components/PrinterGroupCard', () => ({
  PrinterGroupCard: ({ group, onEdit, onDelete, onSelect }: { group: PrinterGroup; onEdit: (g: PrinterGroup) => void; onDelete: (g: PrinterGroup) => void; onSelect: (g: PrinterGroup) => void }) => (
    <div data-testid={`group-card-${group.id}`}>
      <span>{group.name}</span>
      <button onClick={() => onEdit(group)}>Edit</button>
      <button onClick={() => onDelete(group)}>Delete</button>
      <button onClick={() => onSelect(group)}>Select</button>
    </div>
  ),
}));

vi.mock('../components/PrinterGroupModal', () => ({
  PrinterGroupModal: ({ isOpen }: { isOpen: boolean; onClose: () => void; editGroup?: PrinterGroup | null }) => (
    isOpen ? <div data-testid="printer-group-modal">Modal Open</div> : null
  ),
}));

vi.mock('../components/PrinterGroupDetail', () => ({
  PrinterGroupDetail: ({ groupId, onBack }: { groupId: string; onBack: () => void; onEdit: (g: PrinterGroup) => void; onDelete: (g: PrinterGroup) => void }) => (
    <div data-testid="printer-group-detail" data-group-id={groupId}>
      <button onClick={onBack}>Back</button>
    </div>
  ),
}));

vi.mock('@/common/components/modals/DeleteConfirmationModal', () => ({
  DeleteConfirmationModal: ({ isOpen, onConfirm, onClose }: { isOpen: boolean; onConfirm: () => void; onClose: () => void; title?: string; message?: string; confirmText?: string; isDeleting?: boolean }) => (
    isOpen ? (
      <div data-testid="delete-modal">
        <button onClick={onConfirm}>Confirm Delete</button>
        <button onClick={onClose}>Cancel Delete</button>
      </div>
    ) : null
  ),
}));

import { PrinterGroupsPage } from '../pages/PrinterGroupsPage';

const createQueryClient = () =>
  new QueryClient({ defaultOptions: { queries: { retry: false } } });

describe('PrinterGroupsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockGetPrinterGroups.mockReset();
    mockDeletePrinterGroup.mockReset();
  });

  const renderPage = () =>
    render(
      <QueryClientProvider client={createQueryClient()}>
        <PrinterGroupsPage />
      </QueryClientProvider>,
    );

  it('renders page template with correct title', async () => {
    mockGetPrinterGroups.mockResolvedValue(mockGroups);
    renderPage();
    expect(screen.getByTestId('page-template')).toHaveAttribute('data-title', 'Printer Groups');
  });

  it('shows spinner while loading', () => {
    mockGetPrinterGroups.mockReturnValue(new Promise(() => {}));
    renderPage();
    expect(screen.getByTestId('spinner')).toBeInTheDocument();
  });

  it('shows error message on fetch failure', async () => {
    mockGetPrinterGroups.mockRejectedValue(new Error('Network error'));
    renderPage();
    await waitFor(() => {
      expect(screen.getByText(/Failed to load groups/)).toBeInTheDocument();
    });
  });

  it('shows empty state when no groups exist', async () => {
    mockGetPrinterGroups.mockResolvedValue([]);
    renderPage();
    await waitFor(() => {
      expect(screen.getByText('No printer groups yet')).toBeInTheDocument();
    });
  });

  it('shows Create First Group button in empty state', async () => {
    mockGetPrinterGroups.mockResolvedValue([]);
    renderPage();
    await waitFor(() => {
      expect(screen.getByText('Create First Group')).toBeInTheDocument();
    });
  });

  it('renders group cards for each group', async () => {
    mockGetPrinterGroups.mockResolvedValue(mockGroups);
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId('group-card-g1')).toBeInTheDocument();
      expect(screen.getByTestId('group-card-g2')).toBeInTheDocument();
    });
  });

  it('shows Create Group action button', async () => {
    mockGetPrinterGroups.mockResolvedValue(mockGroups);
    renderPage();
    await waitFor(() => {
      expect(screen.getByText('Create Group')).toBeInTheDocument();
    });
  });

  it('opens modal when Create Group is clicked', async () => {
    mockGetPrinterGroups.mockResolvedValue(mockGroups);
    renderPage();
    await waitFor(() => screen.getByText('Create Group'));
    fireEvent.click(screen.getByText('Create Group'));
    expect(screen.getByTestId('printer-group-modal')).toBeInTheDocument();
  });

  it('opens modal when edit is clicked on a card', async () => {
    mockGetPrinterGroups.mockResolvedValue(mockGroups);
    renderPage();
    await waitFor(() => screen.getByTestId('group-card-g1'));
    fireEvent.click(screen.getAllByText('Edit')[0]);
    expect(screen.getByTestId('printer-group-modal')).toBeInTheDocument();
  });

  it('opens delete confirmation when delete is clicked on a card', async () => {
    mockGetPrinterGroups.mockResolvedValue(mockGroups);
    renderPage();
    await waitFor(() => screen.getByTestId('group-card-g1'));
    fireEvent.click(screen.getAllByText('Delete')[0]);
    expect(screen.getByTestId('delete-modal')).toBeInTheDocument();
  });

  it('switches to detail view when a group is selected', async () => {
    mockGetPrinterGroups.mockResolvedValue(mockGroups);
    renderPage();
    await waitFor(() => screen.getByTestId('group-card-g1'));
    fireEvent.click(screen.getAllByText('Select')[0]);
    expect(screen.getByTestId('printer-group-detail')).toBeInTheDocument();
    expect(screen.getByTestId('printer-group-detail')).toHaveAttribute('data-group-id', 'g1');
  });

  it('returns to list view when back is clicked in detail', async () => {
    mockGetPrinterGroups.mockResolvedValue(mockGroups);
    renderPage();
    await waitFor(() => screen.getByTestId('group-card-g1'));
    fireEvent.click(screen.getAllByText('Select')[0]);
    expect(screen.getByTestId('printer-group-detail')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Back'));
    await waitFor(() => {
      expect(screen.getByTestId('group-card-g1')).toBeInTheDocument();
    });
  });

  it('calls delete API when confirming deletion', async () => {
    mockGetPrinterGroups.mockResolvedValue(mockGroups);
    mockDeletePrinterGroup.mockResolvedValue(undefined);
    renderPage();
    await waitFor(() => screen.getByTestId('group-card-g1'));
    fireEvent.click(screen.getAllByText('Delete')[0]);
    fireEvent.click(screen.getByText('Confirm Delete'));
    await waitFor(() => {
      expect(mockDeletePrinterGroup).toHaveBeenCalledWith('g1');
    });
  });

  it('closes delete modal when cancel is clicked', async () => {
    mockGetPrinterGroups.mockResolvedValue(mockGroups);
    renderPage();
    await waitFor(() => screen.getByTestId('group-card-g1'));
    fireEvent.click(screen.getAllByText('Delete')[0]);
    expect(screen.getByTestId('delete-modal')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Cancel Delete'));
    expect(screen.queryByTestId('delete-modal')).not.toBeInTheDocument();
  });
});
