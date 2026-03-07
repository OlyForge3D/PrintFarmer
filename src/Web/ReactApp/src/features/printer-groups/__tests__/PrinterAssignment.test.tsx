import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { PrinterGroupPrinter } from '@/types/api';

const { mockToast, mockAssignPrinterToGroup, mockRemovePrinterFromGroup } = vi.hoisted(() => ({
  mockToast: { success: vi.fn(), error: vi.fn() },
  mockAssignPrinterToGroup: vi.fn(),
  mockRemovePrinterFromGroup: vi.fn(),
}));

vi.mock('sonner', () => ({ toast: mockToast }));
vi.mock('@/services/api', () => ({
  apiClient: {
    assignPrinterToGroup: (...args: unknown[]) => mockAssignPrinterToGroup(...args),
    removePrinterFromGroup: (...args: unknown[]) => mockRemovePrinterFromGroup(...args),
  },
}));

const mockAllPrinters = [
  { id: 'p1', name: 'Printer One', backend: 'Moonraker' },
  { id: 'p2', name: 'Printer Two', backend: 'PrusaLink' },
  { id: 'p3', name: 'Printer Three', backend: 'Moonraker' },
];

vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: mockAllPrinters, isLoading: false }),
}));

vi.mock('@/common/components/ui', () => ({
  Button: ({ children, onClick, disabled, loading, ...rest }: React.ButtonHTMLAttributes<HTMLButtonElement> & { variant?: string; size?: string; loading?: boolean; iconLeft?: React.ReactNode }) => (
    <button onClick={onClick} disabled={disabled || loading} {...rest}>{rest.iconLeft}{children}</button>
  ),
  Badge: ({ children }: { children: React.ReactNode; variant?: string; size?: string }) => (
    <span data-testid="badge">{children}</span>
  ),
  Select: ({ children, id, value, onChange, disabled }: React.SelectHTMLAttributes<HTMLSelectElement> & { containerClassName?: string }) => (
    <select id={id} value={value} onChange={onChange} disabled={disabled}>{children}</select>
  ),
  FormField: ({ children, label, htmlFor, className }: { children: React.ReactNode; label: string; htmlFor: string; className?: string }) => (
    <div className={className}><label htmlFor={htmlFor}>{label}</label>{children}</div>
  ),
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  PlusIcon: () => <span data-testid="plus-icon" />,
  DeleteIcon: () => <span data-testid="delete-icon" />,
}));

vi.mock('@/types/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@/types/api')>();
  return { ...orig };
});

import { PrinterAssignment } from '../components/PrinterAssignment';

const createQueryClient = () =>
  new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

const assignedPrinters: PrinterGroupPrinter[] = [
  { id: 'p1', name: 'Printer One', backend: 1, isAvailable: true, inMaintenance: false },
];

describe('PrinterAssignment', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockAssignPrinterToGroup.mockReset();
    mockRemovePrinterFromGroup.mockReset();
  });

  const renderAssignment = (props?: { groupId?: string; assignedPrinters?: PrinterGroupPrinter[] }) =>
    render(
      <QueryClientProvider client={createQueryClient()}>
        <PrinterAssignment
          groupId={props?.groupId ?? 'g1'}
          assignedPrinters={props?.assignedPrinters ?? assignedPrinters}
        />
      </QueryClientProvider>,
    );

  it('renders Add Printer label', () => {
    renderAssignment();
    expect(screen.getByText('Add Printer')).toBeInTheDocument();
  });

  it('renders Assigned Printers heading', () => {
    renderAssignment();
    expect(screen.getByText('Assigned Printers')).toBeInTheDocument();
  });

  it('displays assigned printer names', () => {
    renderAssignment();
    expect(screen.getByText('Printer One')).toBeInTheDocument();
  });

  it('shows empty state when no printers are assigned', () => {
    renderAssignment({ assignedPrinters: [] });
    expect(screen.getByText('No printers assigned to this group')).toBeInTheDocument();
  });

  it('filters out already assigned printers from dropdown', () => {
    renderAssignment();
    const select = screen.getByRole('combobox');
    const options = Array.from(select.querySelectorAll('option'));
    const optionTexts = options.map((o) => o.textContent);
    // p1 (Printer One) is assigned, so it should not appear; p2 and p3 should
    expect(optionTexts.some((t) => t?.includes('Printer One'))).toBe(false);
    expect(optionTexts.some((t) => t?.includes('Printer Two'))).toBe(true);
    expect(optionTexts.some((t) => t?.includes('Printer Three'))).toBe(true);
  });

  it('shows No available printers when all are assigned', () => {
    const allAssigned: PrinterGroupPrinter[] = mockAllPrinters.map((p) => ({
      id: p.id,
      name: p.name,
      backend: 1,
      isAvailable: true,
      inMaintenance: false,
    }));
    renderAssignment({ assignedPrinters: allAssigned });
    const select = screen.getByRole('combobox');
    const options = Array.from(select.querySelectorAll('option'));
    expect(options[0].textContent).toBe('No available printers');
  });

  it('disables assign button when no printer is selected', () => {
    renderAssignment();
    const assignBtn = screen.getByText('Assign');
    expect(assignBtn).toBeDisabled();
  });

  it('calls assignPrinterToGroup when a printer is selected and assigned', async () => {
    mockAssignPrinterToGroup.mockResolvedValue(undefined);
    renderAssignment();

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'p2' } });
    fireEvent.click(screen.getByText('Assign'));

    await waitFor(() => {
      expect(mockAssignPrinterToGroup).toHaveBeenCalledWith('g1', 'p2');
    });
  });

  it('shows success toast on assign', async () => {
    mockAssignPrinterToGroup.mockResolvedValue(undefined);
    renderAssignment();

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'p2' } });
    fireEvent.click(screen.getByText('Assign'));

    await waitFor(() => {
      expect(mockToast.success).toHaveBeenCalledWith('Printer assigned to group');
    });
  });

  it('calls removePrinterFromGroup when remove is clicked', async () => {
    mockRemovePrinterFromGroup.mockResolvedValue(undefined);
    renderAssignment();

    fireEvent.click(screen.getByText('Remove'));

    await waitFor(() => {
      expect(mockRemovePrinterFromGroup).toHaveBeenCalledWith('g1', 'p1');
    });
  });

  it('shows success toast on remove', async () => {
    mockRemovePrinterFromGroup.mockResolvedValue(undefined);
    renderAssignment();

    fireEvent.click(screen.getByText('Remove'));

    await waitFor(() => {
      expect(mockToast.success).toHaveBeenCalledWith('Printer removed from group');
    });
  });

  it('shows error toast on assign failure', async () => {
    mockAssignPrinterToGroup.mockRejectedValue({ message: 'Assign failed' });
    renderAssignment();

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'p2' } });
    fireEvent.click(screen.getByText('Assign'));

    await waitFor(() => {
      expect(mockToast.error).toHaveBeenCalled();
    });
  });

  it('shows error toast on remove failure', async () => {
    mockRemovePrinterFromGroup.mockRejectedValue({ message: 'Remove failed' });
    renderAssignment();

    fireEvent.click(screen.getByText('Remove'));

    await waitFor(() => {
      expect(mockToast.error).toHaveBeenCalled();
    });
  });

  it('renders maintenance badge for printer in maintenance', () => {
    renderAssignment({
      assignedPrinters: [
        { id: 'p1', name: 'Printer One', backend: 1, isAvailable: true, inMaintenance: true },
      ],
    });
    expect(screen.getByText('Maintenance')).toBeInTheDocument();
  });

  it('renders offline badge for unavailable printer', () => {
    renderAssignment({
      assignedPrinters: [
        { id: 'p1', name: 'Printer One', backend: 1, isAvailable: false, inMaintenance: false },
      ],
    });
    expect(screen.getByText('Offline')).toBeInTheDocument();
  });

  it('does not show offline badge for printer in maintenance', () => {
    renderAssignment({
      assignedPrinters: [
        { id: 'p1', name: 'Printer One', backend: 1, isAvailable: false, inMaintenance: true },
      ],
    });
    expect(screen.getByText('Maintenance')).toBeInTheDocument();
    expect(screen.queryByText('Offline')).not.toBeInTheDocument();
  });

  it('renders remove button for each assigned printer', () => {
    const twoPrinters: PrinterGroupPrinter[] = [
      { id: 'p1', name: 'Printer One', backend: 1, isAvailable: true, inMaintenance: false },
      { id: 'p3', name: 'Printer Three', backend: 1, isAvailable: true, inMaintenance: false },
    ];
    renderAssignment({ assignedPrinters: twoPrinters });
    expect(screen.getAllByText('Remove')).toHaveLength(2);
  });
});
