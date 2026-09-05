import '@testing-library/jest-dom';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { PrinterTableView } from '@/features/printers/components/PrinterTableView';
import { PrinterBackend, type AutoDispatchStatus, type Printer } from '@/types/api';

const { useAllAutoDispatchStatusesMock } = vi.hoisted(() => ({
  useAllAutoDispatchStatusesMock: vi.fn(),
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock('@/common/hooks/usePrinterDisplay', () => ({
  usePrinterDisplays: (printers: Printer[]) => printers,
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAllAutoDispatchStatuses: () => useAllAutoDispatchStatusesMock(),
}));

describe('PrinterTableView - maintenance button', () => {
  afterEach(() => {
    vi.clearAllMocks();
    useAllAutoDispatchStatusesMock.mockReturnValue({ data: [] as AutoDispatchStatus[] });
  });

  const basePrinter: Printer = {
    id: 'p1',
    name: 'Printer 1',
    backendUrl: 'http://printer.local',
    isOnline: true,
    isReachable: true,
    backend: PrinterBackend.Moonraker,
    manufacturerName: 'Prusa',
    modelName: 'MK3S',
    ipAddress: '192.168.1.2',
    inMaintenance: false,
    isEnabled: true,
  };

  useAllAutoDispatchStatusesMock.mockReturnValue({ data: [] as AutoDispatchStatus[] });

  it.each([0, 42.6, 100])('renders numeric progress %s with its percentage and accessible value', (progress) => {
    render(
      <PrinterTableView
        printers={[{ ...basePrinter, state: 'Printing', progress }]}
        onEdit={vi.fn()}
        onDelete={vi.fn()}
        onBulkSetMaintenance={vi.fn()}
        onOpenMaintenance={vi.fn()}
      />
    );

    expect(screen.getByText(`${Math.round(progress)}%`)).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Print progress' })).toHaveAttribute('aria-valuenow', String(Math.round(progress)));
  });

  it.each([
    ['absent', {}],
    ['undefined', { progress: undefined }],
    ['null', { progress: null }],
    ['negative', { progress: -1 }],
    ['unknown', { progress: Number.NaN }],
  ])('keeps the missing-progress placeholder for %s progress', (_label, overrides) => {
    // Runtime JSON can contain null even though the API type only declares an optional number.
    const printer = { ...basePrinter, state: 'Printing', ...overrides } as Printer;

    render(
      <PrinterTableView
        printers={[printer]}
        onEdit={vi.fn()}
        onDelete={vi.fn()}
        onBulkSetMaintenance={vi.fn()}
        onOpenMaintenance={vi.fn()}
      />
    );

    const row = screen.getByRole('row', { name: /Printer 1/ });
    expect(within(row).getByRole('cell', { name: '—', exact: true })).toBeInTheDocument();
    expect(within(row).queryByRole('progressbar')).not.toBeInTheDocument();
    expect(within(row).queryByText('0%')).not.toBeInTheDocument();
  });

  it('opens maintenance actions (does not toggle maintenance mode)', () => {
    const onOpenMaintenance = vi.fn();
    const onBulkSetMaintenance = vi.fn();

    render(
      <PrinterTableView
        printers={[basePrinter]}
        onEdit={vi.fn()}
        onDelete={vi.fn()}
        onBulkSetMaintenance={onBulkSetMaintenance}
        onOpenMaintenance={onOpenMaintenance}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: 'Maintenance' }));

    expect(onOpenMaintenance).toHaveBeenCalledTimes(1);
    expect(onOpenMaintenance).toHaveBeenCalledWith(basePrinter);
    expect(onBulkSetMaintenance).not.toHaveBeenCalled();
  });

  it('surfaces Pending Ready status when auto-dispatch is waiting for bed clear confirmation', () => {
    useAllAutoDispatchStatusesMock.mockReturnValue({
      data: [{
        printerId: 'p1',
        enabled: true,
        state: 'PendingReady',
        queueDepth: 2,
      } satisfies AutoDispatchStatus],
    });

    render(
      <PrinterTableView
        printers={[{ ...basePrinter, state: 'Complete' }]}
        onEdit={vi.fn()}
        onDelete={vi.fn()}
        onBulkSetMaintenance={vi.fn()}
        onOpenMaintenance={vi.fn()}
      />
    );

    expect(screen.getByText('Pending Ready')).toBeInTheDocument();
    expect(screen.getByText('Awaiting bed clear • 2 queued')).toBeInTheDocument();
  });

  it('collapses the backend "Unknown" / "Unknown Model" sentinel pair into a single coherent subtitle', () => {
    render(
      <PrinterTableView
        printers={[{ ...basePrinter, manufacturerName: 'Unknown', modelName: 'Unknown Model' }]}
        onEdit={vi.fn()}
        onDelete={vi.fn()}
        onBulkSetMaintenance={vi.fn()}
        onOpenMaintenance={vi.fn()}
      />
    );

    expect(screen.getByText('Unknown model')).toBeInTheDocument();
    expect(screen.queryByText(/unknown.*unknown/i)).not.toBeInTheDocument();
  });
});
