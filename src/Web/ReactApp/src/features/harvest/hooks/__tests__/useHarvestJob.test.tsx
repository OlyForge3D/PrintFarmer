import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useHarvestJob, HARVEST_INVALIDATION_KEYS } from '../useHarvestJob';
import { configurePartsHarvestClient } from '@/services/partsHarvest';

interface StubClient {
  get: ReturnType<typeof vi.fn>;
  post: ReturnType<typeof vi.fn>;
}

function makeStubClient(): StubClient {
  return { get: vi.fn(), post: vi.fn() };
}

function makeWrapper(client: QueryClient) {
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
}

describe('useHarvestJob', () => {
  let stub: StubClient;
  let queryClient: QueryClient;

  beforeEach(() => {
    stub = makeStubClient();
    configurePartsHarvestClient(stub);
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, gcTime: 0 },
        mutations: { retry: false },
      },
    });
  });

  it('invalidates every documented query key on success', async () => {
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    stub.post.mockResolvedValueOnce({
      data: {
        printJobId: 'job-1',
        harvestedAt: '2026-01-01T00:00:00Z',
        alreadyHarvested: false,
        adjustments: [],
        outputs: [],
      },
    });

    const { result } = renderHook(() => useHarvestJob(), {
      wrapper: makeWrapper(queryClient),
    });

    result.current.mutate({ jobId: 'job-1', request: {} });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    for (const key of HARVEST_INVALIDATION_KEYS) {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: [...key] });
    }
  });

  it('does not invalidate queries on error', async () => {
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    stub.post.mockRejectedValueOnce(
      Object.assign(new Error('boom'), {
        isAxiosError: true,
        response: { status: 500, data: { detail: 'boom' } },
      }),
    );

    const { result } = renderHook(() => useHarvestJob(), {
      wrapper: makeWrapper(queryClient),
    });

    result.current.mutate({ jobId: 'job-1', request: {} });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(invalidateSpy).not.toHaveBeenCalled();
  });
});
