import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { toast } from 'sonner';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { PrinterFileCatalogPage, PrinterFileScanStatus } from '@/types/api';
import { PrinterFileDiscoverySection } from '@/features/printers/components/PrinterFileDiscoverySection';

const mocks = vi.hoisted(() => ({
  catalog: vi.fn(), refresh: vi.fn(), enabled: vi.fn(), hasPermission: vi.fn(),
}));
vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterFileCatalog: mocks.catalog,
    refreshPrinterFileIndex: mocks.refresh,
    setPrinterFileIndexEnabled: mocks.enabled,
  },
}));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: mocks.hasPermission }),
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const completeScan: PrinterFileScanStatus = {
  printerId: 'printer-1', enabled: true, outcome: 'Complete', reasonCode: null,
  lastAttemptAtUtc: '2026-10-25T12:00:00Z', lastCompleteScanAtUtc: '2026-10-25T12:00:00Z',
  nextScanAtUtc: null, configurationGeneration: 1, isStale: false, isScanning: false,
};
function page(overrides: Partial<PrinterFileCatalogPage> = {}): PrinterFileCatalogPage {
  return {
    items: [], total: 0, page: 1, pageSize: 1,
    printers: [{ id: 'printer-1', name: 'Voron', canManage: true, canCopy: true, canDelete: false, supportsCopy: true }],
    scans: [completeScan],
    automaticDiscoveryEnabled: true,
    ...overrides,
  };
}

/** Rendered inside a form, as in the Edit printer modal, to prove nothing here submits it. */
function renderSection(onSubmit = vi.fn((event: SubmitEvent) => event.preventDefault())) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <form aria-label="Edit printer" onSubmit={(event) => onSubmit(event.nativeEvent as SubmitEvent)}>
        <PrinterFileDiscoverySection printerId="printer-1" />
      </form>
    </QueryClientProvider>,
  );
  return { onSubmit, queryClient };
}

function statusValue(term: string) {
  const definition = screen.getByText(term, { selector: 'dt' }).nextElementSibling;
  if (!(definition instanceof HTMLElement)) throw new Error(`no value for ${term}`);
  return definition;
}

describe('PrinterFileDiscoverySection', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    mocks.hasPermission.mockReturnValue(true);
    mocks.catalog.mockResolvedValue(page());
    mocks.refresh.mockResolvedValue(undefined);
    mocks.enabled.mockResolvedValue(undefined);
  });

  it('reads this printer\'s status from a one-item catalog page and shows dates date-only', async () => {
    renderSection();
    expect(await screen.findByRole('heading', { name: 'Files' })).toBeInTheDocument();
    await waitFor(() => expect(statusValue('Status')).toHaveTextContent(/^Complete$/));
    expect(mocks.catalog).toHaveBeenCalledWith({ printerId: 'printer-1', pageSize: 1 }, expect.any(AbortSignal));
    const completedAt = new Date('2026-10-25T12:00:00Z');
    const time = within(statusValue('Last complete scan')).getByText(completedAt.toLocaleDateString());
    expect(time.tagName).toBe('TIME');
    expect(time).toHaveAttribute('title', completedAt.toLocaleString());
    expect(screen.getByText(/Changes apply immediately and are not saved with this form/)).toBeInTheDocument();
    expect(screen.getByText(/Admin Settings > Files/)).toBeInTheDocument();
  });

  it('applies the automatic-discovery toggle immediately without submitting the printer form', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderSection();
    const toggle = await screen.findByRole('checkbox', { name: 'Automatic file discovery' });
    expect(toggle).toBeChecked();
    await user.click(toggle);
    await waitFor(() => expect(mocks.enabled).toHaveBeenCalledWith('printer-1', false));
    expect(mocks.enabled).toHaveBeenCalledTimes(1);
    expect(mocks.refresh).not.toHaveBeenCalled();
    expect(onSubmit).not.toHaveBeenCalled();
    // The catalog (and this status) is refreshed from the server after the change.
    await waitFor(() => expect(mocks.catalog).toHaveBeenCalledTimes(2));
  });

  it('force refreshes with a non-submitting button and claims success only after the scan completes', async () => {
    const user = userEvent.setup();
    const { onSubmit, queryClient } = renderSection();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    let complete!: () => void;
    mocks.refresh.mockReturnValue(new Promise<void>(resolve => { complete = resolve; }));
    const refresh = await screen.findByRole('button', { name: 'Force refresh' });
    expect(refresh).toHaveAttribute('type', 'button');
    expect(refresh).toHaveAttribute('title', expect.stringMatching(/Requires an idle printer and a free I\/O slot/));
    await user.click(refresh);
    await waitFor(() => expect(mocks.refresh).toHaveBeenCalledWith('printer-1', true));
    expect(screen.getByRole('checkbox', { name: 'Automatic file discovery' })).toBeDisabled();
    expect(toast.success).not.toHaveBeenCalled();
    expect(invalidate).not.toHaveBeenCalled();

    complete();
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Force refresh completed. Previews reload on demand.'));
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ['printer-file-thumbnail'] }));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['printer-file-catalog'] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['printer-file-preview'] });
    expect(mocks.refresh).toHaveBeenCalledTimes(1);
    expect(mocks.enabled).not.toHaveBeenCalled();
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('reports why a force refresh did not run instead of claiming success', async () => {
    const user = userEvent.setup();
    const { queryClient } = renderSection();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    mocks.refresh.mockRejectedValue({
      statusCode: 409,
      message: 'Request failed with status code 409',
      data: { code: 'waiting_for_confirmed_idle', reasonCode: 'waiting_for_confirmed_idle' },
    });
    await user.click(await screen.findByRole('button', { name: 'Force refresh' }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Force refresh failed: waiting for confirmed idle'));
    expect(toast.success).not.toHaveBeenCalled();
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ['printer-file-catalog'] }));
    expect(invalidate).not.toHaveBeenCalledWith({ queryKey: ['printer-file-preview'] });
  });

  it('defaults discovery on for a never-scanned printer and does not claim a completed scan', async () => {
    mocks.catalog.mockResolvedValue(page({ scans: [] }));
    renderSection();
    expect(await screen.findByRole('checkbox', { name: 'Automatic file discovery' })).toBeChecked();
    expect(statusValue('Status')).toHaveTextContent(/^Never scanned$/);
    expect(statusValue('Last complete scan')).toHaveTextContent(/^Never$/);
  });

  it('reports a disabled, stale, deferred printer honestly', async () => {
    mocks.catalog.mockResolvedValue(page({
      scans: [{ ...completeScan, enabled: false, outcome: 'Deferred', reasonCode: 'printer_busy', isStale: true,
        nextScanAtUtc: '2026-10-26T12:00:00Z' }],
      automaticDiscoveryEnabled: false,
    }));
    renderSection();
    expect(await screen.findByRole('checkbox', { name: 'Automatic file discovery' })).not.toBeChecked();
    expect(statusValue('Status')).toHaveTextContent(/^Deferred · Stale$/);
    expect(statusValue('Reason')).toHaveTextContent(/^printer busy$/);
    expect(statusValue('Next eligible scan')).toHaveTextContent(new Date('2026-10-26T12:00:00Z').toLocaleDateString());
    expect(screen.getByText(/globally disabled/)).toBeInTheDocument();
  });

  it('shows status without controls when the server denies management of this printer', async () => {
    mocks.catalog.mockResolvedValue(page({ printers: [{ ...page().printers[0], canManage: false }] }));
    renderSection();
    expect(await screen.findByText(/do not have permission to change file discovery/)).toBeInTheDocument();
    expect(statusValue('Status')).toHaveTextContent(/^Complete$/);
    expect(screen.queryByRole('checkbox', { name: 'Automatic file discovery' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Force refresh' })).not.toBeInTheDocument();
  });

  it('renders and fetches nothing without printer-file read permission', () => {
    mocks.hasPermission.mockReturnValue(false);
    renderSection();
    expect(screen.queryByRole('heading', { name: 'Files' })).not.toBeInTheDocument();
    expect(mocks.hasPermission).toHaveBeenCalledWith('printer_files', 'read');
    expect(mocks.catalog).not.toHaveBeenCalled();
  });
});