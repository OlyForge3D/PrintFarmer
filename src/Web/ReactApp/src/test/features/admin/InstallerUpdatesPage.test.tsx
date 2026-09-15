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
  beforeEach(() => { vi.clearAllMocks(); });
  it('makes exactly one explicit request for one online event', async () => {
    getSystemInfo.mockResolvedValue({ inventory: inventory() });
    renderPage();
    await screen.findByTestId('installer-updates');
    expect(getSystemInfo).toHaveBeenCalledOnce();

    await act(async () => { window.dispatchEvent(new Event('online')); });
    await waitFor(() => expect(getSystemInfo).toHaveBeenCalledTimes(2));
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
});
