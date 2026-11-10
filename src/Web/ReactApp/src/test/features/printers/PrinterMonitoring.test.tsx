import { render, renderHook, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { PropsWithChildren } from 'react';
import { PrinterMonitoringControl } from '@/features/printers/components/PrinterMonitoringControl';
import PrinterBulkControls from '@/features/printers/components/admin/PrinterBulkControls';
import { usePrinterMonitoring } from '@/features/printers/hooks/usePrinterMonitoring';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import { PrinterBackend, type Printer } from '@/types/api';

const auth = vi.hoisted(() => ({ allowed: true }));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => auth.allowed }),
}));
vi.mock('@/services/api', () => ({ apiClient: { setPrinterExpectedOffline: vi.fn() } }));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

function printer(id: string, extra: Partial<Printer> = {}): Printer {
  return { id, name: `Printer ${id}`, backend: PrinterBackend.Moonraker, isOnline: false, state: 'Offline', rowVersion: 'revision', ...extra };
}

function wrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return { client, Wrapper: ({ children }: PropsWithChildren) => <QueryClientProvider client={client}>{children}</QueryClientProvider> };
}

describe('Expected offline monitoring controls', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    auth.allowed = true;
    vi.mocked(apiClient.setPrinterExpectedOffline).mockResolvedValue({
      id: '1', expectedOfflineSinceUtc: null, rowVersion: 'new-revision',
    });
  });

  it('marks a known offline printer and uses its reviewed revision', async () => {
    const { Wrapper } = wrapper();
    render(<PrinterMonitoringControl printer={printer('1')} />, { wrapper: Wrapper });
    await userEvent.setup().click(screen.getByRole('button', { name: 'Mark expected offline' }));
    await waitFor(() => expect(apiClient.setPrinterExpectedOffline).toHaveBeenCalledWith('1', true, 'revision'));
  });

  it('offers neutral status and explicit reversal without printer-admin permission', () => {
    auth.allowed = false;
    const { Wrapper } = wrapper();
    render(<PrinterMonitoringControl printer={printer('1', { expectedOfflineSinceUtc: '2026-11-10T00:00:00Z' })} />, { wrapper: Wrapper });
    expect(screen.getByText('Expected offline')).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it.each([{ isOnline: true, state: 'Printing' }, { state: 'Unknown' }, { state: 'Error' }])(
    'does not offer acknowledgement for unsafe status %o', extra => {
      const { Wrapper } = wrapper();
      render(<PrinterMonitoringControl printer={printer('1', extra)} />, { wrapper: Wrapper });
      expect(screen.queryByRole('button')).not.toBeInTheDocument();
    },
  );

  it('disables acknowledgement without a reviewed revision', () => {
    const { Wrapper } = wrapper();
    render(<PrinterMonitoringControl printer={printer('1', { rowVersion: null })} />, { wrapper: Wrapper });
    expect(screen.getByRole('button', { name: 'Mark expected offline' })).toBeDisabled();
  });

  it('bulk action updates each selected printer and reports partial failures explicitly', async () => {
    const { Wrapper, client } = wrapper();
    const invalidation = vi.spyOn(client, 'invalidateQueries');
    vi.mocked(apiClient.setPrinterExpectedOffline).mockRejectedValueOnce(new Error('Revision conflict'));
    render(<PrinterBulkControls selectedIds={['1', '2']} printersById={{ 1: printer('1'), 2: printer('2') }}
      onDelete={vi.fn()} onBulkSetMaintenance={vi.fn()} />, { wrapper: Wrapper });
    await userEvent.setup().click(screen.getByRole('button', { name: 'Mark expected offline' }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Could not update 1 printer', expect.objectContaining({
      description: expect.stringContaining('Printer 1: Revision conflict'),
    })));
    expect(apiClient.setPrinterExpectedOffline).toHaveBeenNthCalledWith(2, '2', true, 'revision');
    expect(toast.success).toHaveBeenCalledWith('1 printer marked expected offline until reconnect');
    expect(invalidation).toHaveBeenCalledWith({ queryKey: ['printers'] });
    expect(invalidation).toHaveBeenCalledWith({ queryKey: ['admin', 'overview'] });
  });

  it('restores expected-online intent explicitly and never sends unreviewed changes', async () => {
    const { Wrapper } = wrapper();
    const { result } = renderHook(() => usePrinterMonitoring(), { wrapper: Wrapper });
    await result.current.mutateAsync({ printers: [printer('1'), printer('2', { rowVersion: null })], expectedOffline: false });
    expect(apiClient.setPrinterExpectedOffline).toHaveBeenCalledExactlyOnceWith('1', false, 'revision');
    expect(toast.error).toHaveBeenCalledWith('Could not update 1 printer', expect.objectContaining({
      description: expect.stringContaining('Refresh and review again'),
    }));
  });
});
