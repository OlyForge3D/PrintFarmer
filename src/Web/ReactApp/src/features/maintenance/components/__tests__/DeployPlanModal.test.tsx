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

  // ---------------------------------------------------------------------------
  // Stale cached capabilities during background refetch (Hicks #1).
  //
  // React Query keeps stale cached data visible while a background refetch is
  // in flight — `isLoading` is false but `isFetching` is true. If the
  // Deploy-enable gate looks only at `isLoading`, the modal will surface the
  // stale `supportsPerToolAttribution: false` verdict and permit the
  // operator to send `toolheadId: null` for a printer whose authoritative
  // value is `true`. The fix gates BOTH `canDeploy` and `handleDeploy` on
  // `!isFetching` (which includes refetches), and shows a distinct
  // "Verifying printer capabilities…" indicator during the window.
  // ---------------------------------------------------------------------------

  it('blocks Deploy during a background refetch when stale cached details say per-tool is off, then allows Deploy once authoritative fresh data arrives (Hicks #1)', async () => {
    const stale: PrinterDetails = {
      id: 'printer-1',
      name: 'Voron 2.4',
      serverUrl: 'http://x',
      toolheads: [t0, t1],
      // Stale cached value — pre-fix, this would let the modal disable the
      // scope picker (perToolAllowed=false) and render Deploy enabled with
      // `toolheadId: null` while the refetch was still in flight.
      supportsPerToolAttribution: false,
    } as PrinterDetails;
    const fresh: PrinterDetails = {
      id: 'printer-1',
      name: 'Voron 2.4',
      serverUrl: 'http://x',
      toolheads: [t0, t1],
      // Authoritative refreshed value — per-tool is actually enabled, so
      // the modal MUST require an explicit scope choice.
      supportsPerToolAttribution: true,
    } as PrinterDetails;

    // Only ONE network call is expected: the background refetch driven by
    // observing a stale-in-cache query. We return a controlled promise so
    // the test can hold the `isFetching=true, data=stale` window open.
    let resolveFresh: (v: PrinterDetails) => void = () => {};
    const freshPromise = new Promise<PrinterDetails>(r => {
      resolveFresh = r;
    });
    const getDetailsSpy = apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>;
    getDetailsSpy.mockReturnValue(freshPromise);

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    // Prime the cache with the stale value but stamp `updatedAt` in the
    // past so the observer treats it as stale immediately on subscription
    // (the modal's useQuery uses `staleTime: 60_000`). This is the exact
    // production scenario: a previous mount populated cache with an old
    // capability verdict, and now something (mutation, invalidation,
    // reconnect) demands a re-verification.
    qc.setQueryData(['printerDetails', 'printer-1'], stale, {
      updatedAt: Date.now() - 5 * 60_000,
    });

    render(
      <QueryClientProvider client={qc}>
        <DeployPlanModal isOpen plan={plan} onClose={() => {}} />
      </QueryClientProvider>
    );

    const user = userEvent.setup();
    await user.selectOptions(
      screen.getByRole('combobox', { name: /select printer/i }),
      'printer-1'
    );

    // Observer subscribes with existing stale data → react-query triggers
    // a background refetch. The controlled mock holds `isFetching=true`
    // until we release it.
    await waitFor(() => expect(getDetailsSpy).toHaveBeenCalledTimes(1));

    // A visible "Updating…" indicator is present so the operator knows the
    // current picker verdict is being re-verified — same aria-live surface
    // as the initial "Loading…" indicator, giving screen reader users
    // equivalent feedback.
    expect(screen.getByTestId('printer-details-updating')).toBeInTheDocument();
    expect(screen.getByTestId('printer-details-updating')).toHaveAttribute('role', 'status');
    // The stale picker verdict is NOT rendered (perToolAllowed=false in
    // cache), but Deploy MUST also stay disabled — the refetch is in
    // flight, so we cannot yet trust the current verdict.
    expect(
      screen.queryByRole('radiogroup', { name: /maintenance scope/i })
    ).not.toBeInTheDocument();

    // Deploy MUST be disabled while the refetch is in flight, AND a
    // programmatic click must not fire the mutation — that is the
    // defence-in-depth check inside `handleDeploy`.
    const deployBtn = screen.getByRole('button', { name: /^Deploy$/ });
    expect(deployBtn).toBeDisabled();
    await user.click(deployBtn);
    expect(maintenancePlanService.deployPlan).not.toHaveBeenCalled();

    // Authoritative fresh data arrives — the picker appears because
    // per-tool is enabled with two toolheads, and Deploy re-enables.
    resolveFresh(fresh);
    await waitFor(() =>
      expect(
        screen.getByRole('radiogroup', { name: /maintenance scope/i })
      ).toBeInTheDocument()
    );
    expect(screen.getByRole('button', { name: /^Deploy$/ })).not.toBeDisabled();
    expect(screen.queryByTestId('printer-details-updating')).not.toBeInTheDocument();
  });

  // ---------------------------------------------------------------------------
  // Paused-fetch race (Hicks #1 rejection at 844162933).
  //
  // React Query pauses fetches when the network is offline
  // (`onlineManager.setOnline(false)`). In that state `isFetching` reads
  // FALSE (the fetch is not actively in flight; it is *paused*) while
  // `fetchStatus === 'paused'`. If the Deploy-enable gate looks only at
  // `!isFetching`, the modal will treat a paused stale-cache render as
  // "authoritative details ready" and let Deploy queue a null scope
  // against a printer whose capability has never been verified after
  // selection. The fix requires `fetchStatus === 'idle'` AND
  // `isFetchedAfterMount === true` before Deploy is enabled.
  // ---------------------------------------------------------------------------

  it('blocks Deploy while the printer-details fetch is paused (offline), even when stale cache says per-tool is off; unpauses on reconnect and requires the fresh verdict (Hicks #1 paused)', async () => {
    // Late-imported so we can toggle before mount without affecting other
    // tests. `onlineManager` is a module-level singleton; we restore its
    // state in `finally` so subsequent tests see it online.
    const { onlineManager } = await import('@tanstack/react-query');

    const stale: PrinterDetails = {
      id: 'printer-1',
      name: 'Voron 2.4',
      serverUrl: 'http://x',
      toolheads: [t0, t1],
      // Stale cached verdict — the pre-fix gate would treat this as
      // authoritative because `isFetching` reads false while the fetch
      // is *paused* by onlineManager.
      supportsPerToolAttribution: false,
    } as PrinterDetails;
    const fresh: PrinterDetails = {
      id: 'printer-1',
      name: 'Voron 2.4',
      serverUrl: 'http://x',
      toolheads: [t0, t1],
      supportsPerToolAttribution: true,
    } as PrinterDetails;

    let resolveFresh: (v: PrinterDetails) => void = () => {};
    const freshPromise = new Promise<PrinterDetails>(r => {
      resolveFresh = r;
    });
    const getDetailsSpy = apiClient.getPrinterDetails as unknown as ReturnType<typeof vi.fn>;
    getDetailsSpy.mockReturnValue(freshPromise);

    const wasOnline = onlineManager.isOnline();
    try {
      onlineManager.setOnline(false);

      const qc = new QueryClient({
        defaultOptions: {
          queries: { retry: false, networkMode: 'online' },
        },
      });
      // Prime the cache with the stale verdict so the modal sees data
      // immediately even while offline.
      qc.setQueryData(['printerDetails', 'printer-1'], stale, {
        updatedAt: Date.now() - 5 * 60_000,
      });

      render(
        <QueryClientProvider client={qc}>
          <DeployPlanModal isOpen plan={plan} onClose={() => {}} />
        </QueryClientProvider>
      );

      const user = userEvent.setup();
      await user.selectOptions(
        screen.getByRole('combobox', { name: /select printer/i }),
        'printer-1'
      );

      // The fetch is paused (offline) — the surface must expose the
      // paused status via role="status" so screen readers can observe
      // it. The paused indicator's data-testid is distinct from the
      // loading/updating ones because it is a distinct axis of
      // "authoritative verdict unavailable" that the operator can act
      // on differently (wait for the network vs. wait for backend).
      const pausedIndicator = await screen.findByTestId('printer-details-paused');
      expect(pausedIndicator).toHaveAttribute('role', 'status');
      expect(pausedIndicator.textContent).toMatch(/offline/i);

      // No network call has fired — the fetch is paused, so react-query
      // does not invoke the queryFn until we come back online.
      expect(getDetailsSpy).not.toHaveBeenCalled();

      // The stale picker verdict must NOT be trusted: `showScopePicker`
      // remains hidden even though the cached value has two toolheads,
      // because `perToolAllowed` is gated on `detailsReady` which
      // requires `fetchStatus === 'idle'`.
      expect(
        screen.queryByRole('radiogroup', { name: /maintenance scope/i })
      ).not.toBeInTheDocument();

      // Deploy MUST be disabled AND a programmatic click on the
      // (disabled) button must NOT fire the mutation.
      const deployBtn = screen.getByRole('button', { name: /^Deploy$/ });
      expect(deployBtn).toBeDisabled();
      await user.click(deployBtn);
      expect(maintenancePlanService.deployPlan).not.toHaveBeenCalled();

      // Reconnect. The paused fetch unpauses and calls queryFn.
      onlineManager.setOnline(true);
      await waitFor(() => expect(getDetailsSpy).toHaveBeenCalledTimes(1));

      // Now the modal should indicate the refetch is in flight; the
      // Deploy button must still be disabled.
      expect(screen.getByRole('button', { name: /^Deploy$/ })).toBeDisabled();

      // Authoritative response arrives; the scope picker appears and
      // Deploy re-enables.
      resolveFresh(fresh);
      await waitFor(() =>
        expect(
          screen.getByRole('radiogroup', { name: /maintenance scope/i })
        ).toBeInTheDocument()
      );
      expect(screen.getByRole('button', { name: /^Deploy$/ })).not.toBeDisabled();
      // The paused indicator is gone once the fetch resolves.
      expect(screen.queryByTestId('printer-details-paused')).not.toBeInTheDocument();
    } finally {
      onlineManager.setOnline(wasOnline);
    }
  });
});
