/**
 * DeployPlanModal — per-toolhead scope tests
 *
 * Coverage for the remediation of Hicks #1 (split-verdict at frozen SHA
 * 9bd8c20e9): the modal must expose a ToolheadScopePicker on printers whose
 * projection reports `supportsPerToolAttribution === true` AND that have
 * multiple eligible physical toolheads, and must forward the selected
 * `toolheadId` to the deploy mutation so per-tool schedules can actually be
 * created via the UI. Without this the AC1 ("configure maintenance per
 * physical toolhead") cannot be met even though the backend supports it.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DeployPlanModal } from '../DeployPlanModal';
import type { MaintenancePlanDto } from '@/types/maintenance';
import type { PrinterDetails, ToolheadDto } from '@/types/api';
import { ToolheadType } from '@/types/api';

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterDetails: vi.fn(),
  },
}));

vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [{ id: 'printer-1', name: 'Voron 2.4' }] }),
}));

vi.mock('@/services/maintenancePlanService', () => ({
  maintenancePlanService: {
    getScheduleDeployments: vi.fn().mockResolvedValue([]),
    deployPlan: vi.fn().mockResolvedValue({ id: 'sched-new' }),
    deleteScheduleDeployment: vi.fn(),
  },
}));

import { apiClient } from '@/services/api';
import { maintenancePlanService } from '@/services/maintenancePlanService';

const plan: MaintenancePlanDto = {
  id: 'plan-1',
  name: 'Nozzle care',
  description: null,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
  isActive: true,
  ownerId: null,
  ownerName: null,
  tasks: [],
} as unknown as MaintenancePlanDto;

const t0: ToolheadDto = {
  id: 'th-0',
  index: 0,
  name: 'T0',
  isPrimary: true,
  toolheadType: ToolheadType.Physical,
  cumulativePrintHours: 0,
};
const t1: ToolheadDto = {
  id: 'th-1',
  index: 1,
  name: 'T1',
  isPrimary: false,
  toolheadType: ToolheadType.Physical,
  cumulativePrintHours: 0,
};

function renderModal() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <DeployPlanModal isOpen plan={plan} onClose={() => {}} />
    </QueryClientProvider>
  );
}

describe('DeployPlanModal — per-toolhead scope (Hicks #1)', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows the scope picker when the printer supports per-tool attribution and has multiple eligible toolheads', async () => {
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: 'printer-1',
      name: 'Voron 2.4',
      serverUrl: 'http://x',
      toolheads: [t0, t1],
      supportsPerToolAttribution: true,
    } as PrinterDetails);

    renderModal();
    const user = userEvent.setup();

    await user.selectOptions(
      screen.getByRole('combobox', { name: /select printer/i }),
      'printer-1'
    );

    await waitFor(() =>
      expect(screen.getByRole('radiogroup', { name: /maintenance scope/i })).toBeInTheDocument()
    );
    expect(screen.getByLabelText('Printer-wide')).toBeInTheDocument();
    expect(screen.getByLabelText(/T0/)).toBeInTheDocument();
    expect(screen.getByLabelText(/T1/)).toBeInTheDocument();
  });

  it('sends toolheadId=null when the operator keeps the default Printer-wide scope', async () => {
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: 'printer-1',
      name: 'Voron 2.4',
      serverUrl: 'http://x',
      toolheads: [t0, t1],
      supportsPerToolAttribution: true,
    } as PrinterDetails);

    renderModal();
    const user = userEvent.setup();
    await user.selectOptions(
      screen.getByRole('combobox', { name: /select printer/i }),
      'printer-1'
    );
    await waitFor(() =>
      expect(screen.getByRole('radiogroup', { name: /maintenance scope/i })).toBeInTheDocument()
    );

    await user.click(screen.getByRole('button', { name: /^Deploy$/ }));

    await waitFor(() =>
      expect(maintenancePlanService.deployPlan).toHaveBeenCalledTimes(1)
    );
    expect(maintenancePlanService.deployPlan).toHaveBeenCalledWith(
      expect.objectContaining({
        maintenancePlanId: 'plan-1',
        printerId: 'printer-1',
        toolheadId: null,
      })
    );
  });

  it('sends the selected toolheadId when the operator picks a specific tool', async () => {
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: 'printer-1',
      name: 'Voron 2.4',
      serverUrl: 'http://x',
      toolheads: [t0, t1],
      supportsPerToolAttribution: true,
    } as PrinterDetails);

    renderModal();
    const user = userEvent.setup();
    await user.selectOptions(
      screen.getByRole('combobox', { name: /select printer/i }),
      'printer-1'
    );
    await waitFor(() =>
      expect(screen.getByRole('radiogroup', { name: /maintenance scope/i })).toBeInTheDocument()
    );

    await user.click(screen.getByLabelText(/T1/));
    await user.click(screen.getByRole('button', { name: /^Deploy$/ }));

    await waitFor(() =>
      expect(maintenancePlanService.deployPlan).toHaveBeenCalledTimes(1)
    );
    expect(maintenancePlanService.deployPlan).toHaveBeenCalledWith(
      expect.objectContaining({
        maintenancePlanId: 'plan-1',
        printerId: 'printer-1',
        toolheadId: 'th-1',
      })
    );
  });

  it('does NOT show the scope picker when supportsPerToolAttribution is false', async () => {
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: 'printer-1',
      name: 'Voron 2.4',
      serverUrl: 'http://x',
      toolheads: [t0, t1],
      supportsPerToolAttribution: false,
    } as PrinterDetails);

    renderModal();
    const user = userEvent.setup();
    await user.selectOptions(
      screen.getByRole('combobox', { name: /select printer/i }),
      'printer-1'
    );

    // Wait for the query to resolve; picker must remain hidden.
    await waitFor(() => expect(apiClient.getPrinterDetails).toHaveBeenCalled());
    expect(
      screen.queryByRole('radiogroup', { name: /maintenance scope/i })
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /^Deploy$/ }));
    await waitFor(() =>
      expect(maintenancePlanService.deployPlan).toHaveBeenCalledTimes(1)
    );
    // With per-tool off, the modal must send `toolheadId: null` (i.e. NOT
    // send a per-tool value even by accident).
    expect(maintenancePlanService.deployPlan).toHaveBeenCalledWith(
      expect.objectContaining({ toolheadId: null })
    );
  });

  it('disables the Deploy button while the printer capability query is loading (Hicks v2 major #1)', async () => {
    // Never resolves during the assertion window — simulates a still-pending
    // capability query. The Deploy button must be disabled so the operator
    // can't fire a per-tool-capable printer through as printer-wide by
    // accident.
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockImplementation(
      () => new Promise<PrinterDetails>(() => { /* never resolves */ })
    );

    renderModal();
    const user = userEvent.setup();
    await user.selectOptions(
      screen.getByRole('combobox', { name: /select printer/i }),
      'printer-1'
    );

    // A visible loading indicator communicates the pending state.
    await waitFor(() =>
      expect(screen.getByTestId('printer-details-loading')).toBeInTheDocument()
    );
    const deployBtn = screen.getByRole('button', { name: /^Deploy$/ });
    expect(deployBtn).toBeDisabled();

    // And clicking it does not fire the mutation.
    await user.click(deployBtn);
    expect(maintenancePlanService.deployPlan).not.toHaveBeenCalled();
  });

  it('shows a role="alert" retry banner and blocks Deploy when the printer capability query fails (Hicks v2 major #1)', async () => {
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('boom')
    );

    renderModal();
    const user = userEvent.setup();
    await user.selectOptions(
      screen.getByRole('combobox', { name: /select printer/i }),
      'printer-1'
    );

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/could not load printer capabilities/i);
    expect(
      screen.getByRole('button', { name: /retry loading printer capabilities/i })
    ).toBeInTheDocument();

    const deployBtn = screen.getByRole('button', { name: /^Deploy$/ });
    expect(deployBtn).toBeDisabled();
    await user.click(deployBtn);
    expect(maintenancePlanService.deployPlan).not.toHaveBeenCalled();
  });
});
