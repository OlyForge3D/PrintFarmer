import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AutoDispatchGlobalToggle } from '../AutoDispatchGlobalToggle';

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
  success: vi.fn(),
  error: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    get: mocks.get,
    put: mocks.put,
  },
}));

vi.mock('sonner', () => ({
  toast: {
    success: mocks.success,
    error: mocks.error,
  },
}));

const settings = (eTag: string) => ({
  eTag,
  autoDispatchEnabled: false,
  autoDispatchMode: 'Manual',
  idleThresholdSeconds: 30,
  minimumScoreThreshold: 0,
  maxConcurrentDispatches: 1,
  loadBalancingStrategy: 'Balanced',
  updatedAt: '2026-07-28T00:00:00Z',
});

describe('AutoDispatchGlobalToggle stale intent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.get
      .mockResolvedValueOnce({ data: settings('settings-v1') })
      .mockResolvedValue({ data: settings('settings-v2') });
    mocks.put
      .mockRejectedValueOnce(
        Object.assign(new Error('Settings changed.'), { statusCode: 412 })
      )
      .mockResolvedValue({ data: settings('settings-v3') });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('refetches and requires explicit reconfirmation without automatic retry', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <AutoDispatchGlobalToggle />
      </QueryClientProvider>
    );
    const toggle = await screen.findByLabelText('Toggle system auto-dispatch');

    await userEvent.click(toggle);

    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1));
    await waitFor(() =>
      expect(mocks.get).toHaveBeenCalledWith('/dispatch-settings')
    );
    expect(mocks.put.mock.calls[0]?.[2]).toEqual({
      headers: { 'If-Match': '"settings-v1"' },
    });

    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    await userEvent.click(toggle);
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(mocks.put).toHaveBeenCalledTimes(1);

    confirm.mockReturnValue(true);
    await userEvent.click(toggle);
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(2));
    expect(mocks.put.mock.calls[1]?.[2]).toEqual({
      headers: { 'If-Match': '"settings-v2"' },
    });
  });
});
