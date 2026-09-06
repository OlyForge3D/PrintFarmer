import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useAdminOverview } from '../useAdminOverview';
import { client } from '@/services/api/httpClient';
import { loadWireContractFixture } from '@/test/wireContracts';
import type { AdminOverviewDto } from '@/types/adminOverview';

// -----------------------------------------------------------------------------
// Canonical wire-contract corpus (issue #2240): useAdminOverview() is driven
// from the real serialized payload captured in
// fixtures/wire-contracts/api/admin-overview/overview.live-shape.json by
// issue #2238 (GET /admin/overview), instead of a hand-written mock object.
// The corpus is loaded byte-identical and never edited or normalized here —
// see src/Web/ReactApp/src/test/wireContracts.ts.
// -----------------------------------------------------------------------------

vi.mock('@/services/api/httpClient', () => ({
  client: {
    get: vi.fn(),
  },
}));

function makeWrapper(client: QueryClient) {
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
}

function makeClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
}

describe('useAdminOverview — canonical wire-contract corpus (#2240)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('surfaces the corpus live-shape fixture unchanged (GET /admin/overview)', async () => {
    const fixture = loadWireContractFixture<AdminOverviewDto>(
      'api/admin-overview/overview.live-shape.json'
    );
    vi.mocked(client.get).mockResolvedValue({ data: fixture });

    const { result } = renderHook(() => useAdminOverview(), {
      wrapper: makeWrapper(makeClient()),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.get).toHaveBeenCalledWith(
      '/admin/overview',
      expect.objectContaining({ signal: expect.anything() })
    );
    // The hook must pass the DTO through unchanged: no renamed keys, no
    // reshaped subsystems/attention arrays.
    expect(result.current.data).toEqual(fixture);
    expect(result.current.data?.overallStatus).toBe('Healthy');
    expect(result.current.data?.subsystems).toHaveLength(4);
    expect(result.current.data?.attention).toEqual([]);
  });

  it('surfaces a backend error instead of returning stale/empty data', async () => {
    vi.mocked(client.get).mockRejectedValue(new Error('backend down'));

    const { result } = renderHook(() => useAdminOverview(), {
      wrapper: makeWrapper(makeClient()),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(Error);
    expect(result.current.data).toBeUndefined();
  });

  it('respects enabled: false option and does not trigger fetch', async () => {
    const { result } = renderHook(() => useAdminOverview({ enabled: false }), {
      wrapper: makeWrapper(makeClient()),
    });

    expect(result.current.fetchStatus).toBe('idle');
    expect(client.get).not.toHaveBeenCalled();
  });
});
