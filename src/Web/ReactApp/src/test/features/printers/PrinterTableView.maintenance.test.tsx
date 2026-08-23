import '@testing-library/jest-dom';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { PrinterTableView } from '@/features/printers/components/PrinterTableView';
import { PrinterBackend, type AutoDispatchStatus, type Printer } from '@/types/api';

const { useAllAutoDispatchStatusesMock } = vi.hoisted(() => ({
  useAllAutoDispatchStatusesMock: vi.fn(),
}));
const calibrationSetupPromptRenderMock = vi.hoisted(() => vi.fn());

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock('@/common/hooks/usePrinterDisplay', () => ({
  usePrinterDisplays: (printers: Printer[]) => printers,
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAllAutoDispatchStatuses: () => useAllAutoDispatchStatusesMock(),
}));

vi.mock('@/features/printers/components/CalibrationSetupPrompt', () => ({
  CalibrationSetupPrompt: (props: Record<string, unknown>) => {
    calibrationSetupPromptRenderMock(props);
    return null;
  },
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

  it('wires CalibrationSetupPrompt with each row\'s printer id, name, and rowVersion (#1923) so the onboarding affordance opens setup for the correct printer', () => {
    render(
      <PrinterTableView
        printers={[{ ...basePrinter, id: 'printer-77', name: 'Printer Seventy-Seven', rowVersion: 'rv-9' }]}
        onEdit={vi.fn()}
        onDelete={vi.fn()}
        onBulkSetMaintenance={vi.fn()}
        onOpenMaintenance={vi.fn()}
      />
    );

    expect(calibrationSetupPromptRenderMock).toHaveBeenCalledWith(
      expect.objectContaining({
        printerId: 'printer-77',
        printerName: 'Printer Seventy-Seven',
        rowVersion: 'rv-9',
      }),
    );
  });
});
