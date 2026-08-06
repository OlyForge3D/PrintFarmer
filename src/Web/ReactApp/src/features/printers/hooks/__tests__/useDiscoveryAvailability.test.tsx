import '@testing-library/jest-dom';
import React from 'react';
import { renderHook, waitFor, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const hoisted = vi.hoisted(() => ({
  getSettings: vi.fn(),
  hasPermission: vi.fn(() => true),
}));

vi.mock('@/services/api', () => ({
  apiClient: { getSettings: hoisted.getSettings },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: hoisted.hasPermission }),
}));

import {
  useDiscoveryAvailable,
  useNetworkDiscoverySettings,
  DISCOVERY_AVAILABILITY_QUERY_KEY,
} from '../useDiscoveryAvailability';

function wrapper(client: QueryClient) {
  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

function makeClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, refetchOnWindowFocus: false, refetchInterval: false, gcTime: 0 },
    },
  });
}

describe('useDiscoveryAvailability (#1146 item 7)', () => {
  beforeEach(() => {
    hoisted.getSettings.mockReset();
    hoisted.hasPermission.mockReset();
    hoisted.hasPermission.mockReturnValue(true);
    vi.useRealTimers();
  });

  it('is disabled (no request) for non-admin users', () => {
    hoisted.hasPermission.mockReturnValue(false);
    const qc = makeClient();

    renderHook(() => useNetworkDiscoverySettings(), { wrapper: wrapper(qc) });

    expect(hoisted.getSettings).not.toHaveBeenCalled();
  });

  it('requests NetworkDiscovery settings once for admin users', async () => {
    hoisted.getSettings.mockResolvedValueOnce({ enableDiscovery: true, lastHeartbeat: new Date().toISOString() });
    const qc = makeClient();

    const { result } = renderHook(() => useNetworkDiscoverySettings(), { wrapper: wrapper(qc) });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(hoisted.getSettings).toHaveBeenCalledWith('NetworkDiscovery');
  });

  it('is available when discovery is enabled and the heartbeat is recent', async () => {
    hoisted.getSettings.mockResolvedValueOnce({
      enableDiscovery: true,
      lastHeartbeat: new Date(Date.now() - 5_000).toISOString(),
    });
    const qc = makeClient();

    const { result } = renderHook(() => useDiscoveryAvailable(), { wrapper: wrapper(qc) });

    await waitFor(() => expect(result.current).toBe(true));
  });

  it('is unavailable when discovery is disabled even with a fresh heartbeat', async () => {
    hoisted.getSettings.mockResolvedValueOnce({
      enableDiscovery: false,
      lastHeartbeat: new Date().toISOString(),
    });
    const qc = makeClient();

    const { result } = renderHook(() => useDiscoveryAvailable(), { wrapper: wrapper(qc) });

    await waitFor(() => expect(hoisted.getSettings).toHaveBeenCalled());
    expect(result.current).toBe(false);
  });

  it('is unavailable when the heartbeat is stale, even though the cached query data says enabled', async () => {
    hoisted.getSettings.mockResolvedValueOnce({
      enableDiscovery: true,
      lastHeartbeat: new Date(Date.now() - 120_000).toISOString(), // 2 minutes old
    });
    const qc = makeClient();

    const { result } = renderHook(() => useDiscoveryAvailable(), { wrapper: wrapper(qc) });

    await waitFor(() => expect(hoisted.getSettings).toHaveBeenCalled());
    expect(result.current).toBe(false);
  });

  it('is unavailable when there is no heartbeat at all', async () => {
    hoisted.getSettings.mockResolvedValueOnce({ enableDiscovery: true, lastHeartbeat: undefined });
    const qc = makeClient();

    const { result } = renderHook(() => useDiscoveryAvailable(), { wrapper: wrapper(qc) });

    await waitFor(() => expect(hoisted.getSettings).toHaveBeenCalled());
    expect(result.current).toBe(false);
  });

  it('re-evaluates freshness against current time rather than a value cached at fetch time', async () => {
    // A heartbeat that is fresh right now but will have expired by the time
    // we check again — proves the boolean is derived from a periodically
    // re-ticked "now" (see the hook's own comment on why it can't just call
    // `Date.now()` directly during render), not baked into the query's
    // cached payload once and never revisited.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    hoisted.getSettings.mockResolvedValue({
      enableDiscovery: true,
      lastHeartbeat: new Date().toISOString(),
    });
    const qc = makeClient();

    const { result } = renderHook(() => useDiscoveryAvailable(), { wrapper: wrapper(qc) });

    await waitFor(() => expect(result.current).toBe(true));

    // Advance fake time (and the faked `Date` along with it) past the
    // freshness window and past this hook's own re-check interval, without
    // any new fetch completing (this test's QueryClient has refetchInterval
    // disabled) — the cached settings object never changes; only the
    // hook's periodic freshness tick does.
    act(() => {
      vi.advanceTimersByTime(120_000);
    });

    expect(result.current).toBe(false);
    vi.useRealTimers();
  });

  it('uses the documented, stable query key', () => {
    expect(DISCOVERY_AVAILABILITY_QUERY_KEY).toEqual(['network-discovery', 'availability']);
  });
});