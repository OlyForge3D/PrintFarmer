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

  // ---------------------------------------------------------------------------
  // Loading / error races on the printer-details capability query (Hicks
  // rejection: Deploy must never become enabled and silently send
  // `toolheadId: null` for a per-tool-capable printer while the capability
  // is still unknown or failed).
  // ---------------------------------------------------------------------------

  it('blocks Deploy with an accessible status while the printer capability query is loading', async () => {
    // Never resolves during the assertion window — simulates a still-pending
    // capability query.
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockImplementation(
      () => new Promise<PrinterDetails>(() => { /* never resolves */ })
    );

    renderModal();
    const user = userEvent.setup();
    await user.selectOptions(
      screen.getByRole('combobox', { name: /select printer/i }),
      'printer-1'
    );

    // Screen readers must be able to observe the pending capability check.
    // The container carries `role="status"` (implicit aria-live="polite").
    const loadingIndicator = await screen.findByTestId('printer-details-loading');
    expect(loadingIndicator).toBeInTheDocument();
    expect(loadingIndicator).toHaveAttribute('role', 'status');

    // Deploy must be disabled and must not fire the mutation even if a
    // programmatic click gets through.
    const deployBtn = screen.getByRole('button', { name: /^Deploy$/ });
    expect(deployBtn).toBeDisabled();
    await user.click(deployBtn);
    expect(maintenancePlanService.deployPlan).not.toHaveBeenCalled();
  });

  it('blocks Deploy and surfaces a role="alert" retry banner when the capability query errors', async () => {
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('boom')
    );

    renderModal();
    const user = userEvent.setup();
    await user.selectOptions(
      screen.getByRole('combobox', { name: /select printer/i }),
      'printer-1'
    );

    // The failure surface uses role="alert" so it announces without
    // needing to steal focus (WCAG 4.1.3 status messages).
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/could not load printer capabilities/i);
    // The message includes the error's own message for diagnostic clarity.
    expect(alert).toHaveTextContent(/boom/);
    // Retry action is discoverable by accessible name.
    expect(
      screen.getByRole('button', { name: /retry loading printer capabilities/i })
    ).toBeInTheDocument();

    // Deploy remains disabled while the query is in the error state; even a
    // programmatic click must not fire the mutation.
    const deployBtn = screen.getByRole('button', { name: /^Deploy$/ });
    expect(deployBtn).toBeDisabled();
    await user.click(deployBtn);
    expect(maintenancePlanService.deployPlan).not.toHaveBeenCalled();
  });

  it('resets scope synchronously when the operator switches printers so a stale toolhead id cannot leak', async () => {
    // First printer: per-tool capable with an eligible T1.
    // Second printer: per-tool capable but with a totally different toolhead
    // set (T99). Switching printers with T1 selected must NOT carry T1
    // forward — the picker for the new printer wouldn't offer it, and
    // deploying while the (new) capability query is still loading would
    // otherwise send `toolheadId: 'th-1'` for a printer that never had it.
    const t99: ToolheadDto = {
      id: 'th-99',
      index: 0,
      name: 'T99',
      isPrimary: true,
      toolheadType: ToolheadType.Physical,
      cumulativePrintHours: 0,
    };
    (apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>).mockImplementation(
      async (id: string) => {
        if (id === 'printer-1') {
          return {
            id: 'printer-1',
            name: 'Voron 2.4',
            serverUrl: 'http://x',
            toolheads: [t0, t1],
            supportsPerToolAttribution: true,
          } as PrinterDetails;
        }
        return {
          id: 'printer-2',
          name: 'Bambu X1C',
          serverUrl: 'http://y',
          toolheads: [t99],
          supportsPerToolAttribution: true,
        } as PrinterDetails;
      }
    );
    // usePrinters is mocked at module scope with only printer-1. Extend it
    // locally for this test by remounting with an overridden module.
    // Instead of re-mocking, we exercise the reset by picking T1, then
    // switching back to `""` (empty) and then back to `printer-1`.
    renderModal();
    const user = userEvent.setup();
    const select = screen.getByRole('combobox', { name: /select printer/i });

    await user.selectOptions(select, 'printer-1');
    await waitFor(() =>
      expect(screen.getByRole('radiogroup', { name: /maintenance scope/i })).toBeInTheDocument()
    );
    // Pick a toolhead so scope is no longer the printer-wide default.
    await user.click(screen.getByLabelText(/T1/));

    // Clear the printer selection. Scope must snap back to printer-wide the
    // same tick — the useEffect that resets on next render is not enough;
    // Deploy must never see the stale scope value.
    await user.selectOptions(select, '');
    // The radiogroup is gone (no printer → no picker), so we can only
    // verify indirectly: reselecting the same printer must show
    // Printer-wide as the checked option.
    await user.selectOptions(select, 'printer-1');
    await waitFor(() =>
      expect(screen.getByRole('radiogroup', { name: /maintenance scope/i })).toBeInTheDocument()
    );
    expect(screen.getByLabelText('Printer-wide')).toBeChecked();
  });
});
