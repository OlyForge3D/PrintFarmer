import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { PrinterGroup } from '@/types/api';

const { mockToast, mockCreatePrinterGroup, mockUpdatePrinterGroup } = vi.hoisted(() => ({
  mockToast: { success: vi.fn(), error: vi.fn() },
  mockCreatePrinterGroup: vi.fn(),
  mockUpdatePrinterGroup: vi.fn(),
}));

vi.mock('sonner', () => ({ toast: mockToast }));
vi.mock('@/services/api', () => ({
  apiClient: {
    createPrinterGroup: (...args: unknown[]) => mockCreatePrinterGroup(...args),
    updatePrinterGroup: (...args: unknown[]) => mockUpdatePrinterGroup(...args),
  },
}));

vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [], isLoading: false }),
}));

vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({ children, isOpen, title, footer }: { children: React.ReactNode; isOpen: boolean; title: string; footer?: React.ReactNode; onClose: () => void; size?: string }) => (
    isOpen ? <div data-testid="modal" data-title={title}>{children}{footer}</div> : null
  ),
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  SearchIcon: (props: React.SVGAttributes<SVGElement>) => <svg data-testid="search-icon" {...props} />,
}));

vi.mock('@/common/components/ui', () => ({
  Button: ({ children, onClick, loading, disabled, ...rest }: React.ButtonHTMLAttributes<HTMLButtonElement> & { variant?: string; loading?: boolean; iconLeft?: React.ReactNode }) => (
    <button onClick={onClick} disabled={disabled || loading} data-loading={loading} {...rest}>{children}</button>
  ),
  Input: ({ id, value, onChange, placeholder, invalid, disabled, className }: React.InputHTMLAttributes<HTMLInputElement> & { invalid?: boolean }) => (
    <input id={id} value={value} onChange={onChange} placeholder={placeholder} aria-invalid={invalid} disabled={disabled} className={className} />
  ),
  Textarea: ({ id, value, onChange, placeholder, rows, disabled }: React.TextareaHTMLAttributes<HTMLTextAreaElement>) => (
    <textarea id={id} value={value as string} onChange={onChange} placeholder={placeholder} rows={rows} disabled={disabled} />
  ),
  FormField: ({ children, label, htmlFor, error, required }: { children: React.ReactNode; label: string; htmlFor: string; error?: string; required?: boolean }) => (
    <div>
      <label htmlFor={htmlFor}>{label}{required && ' *'}</label>
      {children}
      {error && <span data-testid="field-error">{error}</span>}
    </div>
  ),
  Badge: ({ children }: { children: React.ReactNode; variant?: string; size?: string }) => <span>{children}</span>,
  Select: ({ children, value, onChange, disabled }: React.SelectHTMLAttributes<HTMLSelectElement> & { containerClassName?: string }) => (
    <select value={value} onChange={onChange} disabled={disabled}>{children}</select>
  ),
  Checkbox: ({ checked, onChange, disabled }: { checked?: boolean; onChange?: () => void; disabled?: boolean }) => (
    <input type="checkbox" checked={checked} onChange={onChange} disabled={disabled} />
  ),
}));

import { PrinterGroupModal } from '../components/PrinterGroupModal';

const createQueryClient = () =>
  new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

const editGroup: PrinterGroup = {
  id: 'g1',
  name: 'Existing Group',
  description: 'Existing description',
  createdDate: '2025-01-01T00:00:00Z',
  updatedDate: '2025-01-02T00:00:00Z',
  printerCount: 2,
};

describe('PrinterGroupModal', () => {
  const onClose = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    mockCreatePrinterGroup.mockReset();
    mockUpdatePrinterGroup.mockReset();
  });

  const renderModal = (props: { isOpen: boolean; editGroup?: PrinterGroup | null }) =>
    render(
      <QueryClientProvider client={createQueryClient()}>
        <PrinterGroupModal isOpen={props.isOpen} onClose={onClose} editGroup={props.editGroup} />
      </QueryClientProvider>,
    );

  it('does not render when closed', () => {
    renderModal({ isOpen: false });
    expect(screen.queryByTestId('modal')).not.toBeInTheDocument();
  });

  it('renders create mode with correct title', () => {
    renderModal({ isOpen: true });
    expect(screen.getByTestId('modal')).toHaveAttribute('data-title', 'Create Printer Group');
  });

  it('renders edit mode with correct title', () => {
    renderModal({ isOpen: true, editGroup });
    expect(screen.getByTestId('modal')).toHaveAttribute('data-title', 'Edit Printer Group');
  });

  it('shows Create Group button in create mode', () => {
    renderModal({ isOpen: true });
    expect(screen.getByText('Create Group')).toBeInTheDocument();
  });

  it('shows Save button in edit mode', () => {
    renderModal({ isOpen: true, editGroup });
    expect(screen.getByText('Save')).toBeInTheDocument();
  });

  it('populates form fields in edit mode', () => {
    renderModal({ isOpen: true, editGroup });
    expect(screen.getByDisplayValue('Existing Group')).toBeInTheDocument();
    expect(screen.getByDisplayValue('Existing description')).toBeInTheDocument();
  });

  it('shows empty fields in create mode', () => {
    renderModal({ isOpen: true });
    const nameInput = screen.getByPlaceholderText('e.g., MK4S Fleet');
    expect(nameInput).toHaveValue('');
  });

  it('shows validation error when name is empty on submit', () => {
    renderModal({ isOpen: true });
    fireEvent.click(screen.getByText('Create Group'));
    expect(screen.getByTestId('field-error')).toHaveTextContent('Name is required');
  });

  it('calls createPrinterGroup on submit in create mode', async () => {
    mockCreatePrinterGroup.mockResolvedValue({ id: 'new', name: 'New Group', printerCount: 0 });
    renderModal({ isOpen: true });

    fireEvent.change(screen.getByPlaceholderText('e.g., MK4S Fleet'), { target: { value: 'New Group' } });
    fireEvent.click(screen.getByText('Create Group'));

    await waitFor(() => {
      expect(mockCreatePrinterGroup).toHaveBeenCalledWith({ name: 'New Group', description: undefined });
    });
  });

  it('shows success toast on create', async () => {
    mockCreatePrinterGroup.mockResolvedValue({ id: 'new', name: 'New Group', printerCount: 0 });
    renderModal({ isOpen: true });

    fireEvent.change(screen.getByPlaceholderText('e.g., MK4S Fleet'), { target: { value: 'New Group' } });
    fireEvent.click(screen.getByText('Create Group'));

    await waitFor(() => {
      expect(mockToast.success).toHaveBeenCalledWith('Group "New Group" created');
    });
  });

  it('calls updatePrinterGroup on submit in edit mode', async () => {
    mockUpdatePrinterGroup.mockResolvedValue({ ...editGroup, name: 'Updated Group' });
    renderModal({ isOpen: true, editGroup });

    fireEvent.change(screen.getByDisplayValue('Existing Group'), { target: { value: 'Updated Group' } });
    fireEvent.click(screen.getByText('Save'));

    await waitFor(() => {
      expect(mockUpdatePrinterGroup).toHaveBeenCalledWith('g1', { name: 'Updated Group', description: 'Existing description' });
    });
  });

  it('shows error toast on create failure', async () => {
    mockCreatePrinterGroup.mockRejectedValue({ message: 'Server error' });
    renderModal({ isOpen: true });

    fireEvent.change(screen.getByPlaceholderText('e.g., MK4S Fleet'), { target: { value: 'Fail Group' } });
    fireEvent.click(screen.getByText('Create Group'));

    await waitFor(() => {
      expect(mockToast.error).toHaveBeenCalled();
    });
  });

  it('calls onClose when Cancel is clicked', () => {
    renderModal({ isOpen: true });
    fireEvent.click(screen.getByText('Cancel'));
    expect(onClose).toHaveBeenCalled();
  });

  it('renders search box for printer filtering', () => {
    renderModal({ isOpen: true });
    expect(screen.getByPlaceholderText('Search printers...')).toBeInTheDocument();
  });

  it('renders name field as required', () => {
    renderModal({ isOpen: true });
    expect(screen.getByText(/Name/)).toBeInTheDocument();
  });
});
