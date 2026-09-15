import { act, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { InstallerUpdatesPage } from '@/features/admin/pages/InstallerUpdatesPage';
import { inventory } from '@/test/features/system/serviceInventoryFixture';

const { getSystemInfo } = vi.hoisted(() => ({ getSystemInfo: vi.fn() }));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));
vi.mock('@/services/api', () => ({
  apiClient: { getSystemInfo },
}));

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <InstallerUpdatesPage />
    </QueryClientProvider>,
  );
}

describe('InstallerUpdatesPage reconnect reconciliation', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: true });
  });
  it('makes exactly one explicit request for one online event', async () => {
    getSystemInfo.mockResolvedValue({ inventory: inventory() });
    renderPage();
    await screen.findByTestId('installer-updates');
    expect(getSystemInfo).toHaveBeenCalledOnce();

    await act(async () => { window.dispatchEvent(new Event('online')); });
    await waitFor(() => expect(getSystemInfo).toHaveBeenCalledTimes(2));
    // Flush query notifications too: automatic reconnect is disabled for this query.
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); });
    expect(getSystemInfo).toHaveBeenCalledTimes(2);
  });

  it('shows an explicit unknown state for an initially offline paused query', async () => {
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: false });
    renderPage();

    expect(await screen.findByRole('status', { name: 'Update observation unknown' })).toHaveTextContent(/reconnect and retry/);
    expect(screen.getByRole('button', { name: 'Retry installation observation' })).toBeVisible();
  });

  it('keeps the observation unknown when an explicit retry fails', async () => {
    getSystemInfo.mockRejectedValue(new Error('network unavailable'));
    renderPage();

    await screen.findByRole('button', { name: 'Retry installation observation' });
    await userEvent.setup().click(screen.getByRole('button', { name: 'Retry installation observation' }));
    expect(await screen.findByRole('status', { name: 'Update observation unknown' })).toHaveTextContent(/snapshot is unknown/);
    expect(getSystemInfo.mock.calls.length).toBeGreaterThanOrEqual(2);
  });

  it('keeps a failed reconciliation unknown and allows a successful retry', async () => {
    getSystemInfo.mockResolvedValueOnce({ inventory: inventory() })
      .mockRejectedValueOnce(new Error('network unavailable'))
      .mockResolvedValueOnce({ inventory: inventory() });
    renderPage();
    await screen.findByTestId('installer-updates');

    await act(async () => { window.dispatchEvent(new Event('online')); });
    expect(await screen.findByText('Update observation unknown')).toBeVisible();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Retry installation observation' }));
    await screen.findByTestId('installer-updates');
    expect(getSystemInfo).toHaveBeenCalledTimes(3);
  });

  it('does not let an in-flight reconnect revive connected after an offline event', async () => {
    let completeReconnect: (() => void) | undefined;
    getSystemInfo.mockResolvedValueOnce({ inventory: inventory() }).mockImplementationOnce(
      () => new Promise((resolve) => {
        completeReconnect = () => resolve({ inventory: inventory() });
      }),
    );
    renderPage();
    await screen.findByTestId('installer-updates');

    await act(async () => { window.dispatchEvent(new Event('online')); });
    await waitFor(() => expect(getSystemInfo).toHaveBeenCalledTimes(2));
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: false });
    await act(async () => { window.dispatchEvent(new Event('offline')); });
    await act(async () => { completeReconnect?.(); });

    expect(await screen.findByRole('status', { name: 'Connection observation unknown' })).toHaveTextContent(/browser is disconnected/);
  });

});
