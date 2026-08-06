import '@testing-library/jest-dom';
import React from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const hoisted = vi.hoisted(() => ({
  getObjectsTags: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: { getObjectsTags: hoisted.getObjectsTags },
}));

import {
  useFleetPrinterTags,
  usePrinterTagsFromFleet,
  printerTagsFleetQueryKey,
} from '../usePrinterTagsFleet';

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

describe('usePrinterTagsFleet (#1146 item 1)', () => {
  beforeEach(() => {
    hoisted.getObjectsTags.mockReset();
  });

  it('requests the batched fleet endpoint once with objectType=Printer', async () => {
    hoisted.getObjectsTags.mockResolvedValueOnce([
      { objectId: 'printer-1', tags: [{ id: 'tag-1', name: 'Production' }] },
    ]);
    const qc = makeClient();

    const { result } = renderHook(() => useFleetPrinterTags(), { wrapper: wrapper(qc) });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(hoisted.getObjectsTags).toHaveBeenCalledTimes(1);
    expect(hoisted.getObjectsTags).toHaveBeenCalledWith('Printer', expect.anything());
    expect(result.current.data).toEqual([
      { objectId: 'printer-1', tags: [{ id: 'tag-1', name: 'Production' }] },
    ]);
  });

  it("selects only the requested printer's tags from the shared fleet cache", async () => {
    hoisted.getObjectsTags.mockResolvedValueOnce([
      { objectId: 'printer-1', tags: [{ id: 'tag-1', name: 'Production' }] },
      { objectId: 'printer-2', tags: [{ id: 'tag-2', name: 'Prototype' }] },
    ]);
    const qc = makeClient();

    const { result } = renderHook(() => usePrinterTagsFromFleet('printer-2'), {
      wrapper: wrapper(qc),
    });

    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(result.current.data).toEqual([{ id: 'tag-2', name: 'Prototype' }]);
  });

  it('returns an empty array (not undefined) for a printer with no tags — preserves empty-tag behavior', async () => {
    hoisted.getObjectsTags.mockResolvedValueOnce([
      { objectId: 'printer-1', tags: [] },
    ]);
    const qc = makeClient();

    const { result } = renderHook(() => usePrinterTagsFromFleet('printer-1'), {
      wrapper: wrapper(qc),
    });

    await waitFor(() => expect(result.current.isPending).toBe(false));
    expect(result.current.data).toEqual([]);
  });

  it('returns an empty array for a printer entirely absent from the fleet response', async () => {
    hoisted.getObjectsTags.mockResolvedValueOnce([
      { objectId: 'printer-1', tags: [{ id: 'tag-1', name: 'Production' }] },
    ]);
    const qc = makeClient();

    const { result } = renderHook(() => usePrinterTagsFromFleet('printer-missing'), {
      wrapper: wrapper(qc),
    });

    await waitFor(() => expect(result.current.isPending).toBe(false));
    expect(result.current.data).toEqual([]);
  });

  it('shares one fleet request across multiple concurrent per-printer selectors', async () => {
    hoisted.getObjectsTags.mockResolvedValueOnce([
      { objectId: 'printer-1', tags: [{ id: 'tag-1', name: 'Production' }] },
      { objectId: 'printer-2', tags: [] },
    ]);
    const qc = makeClient();

    const { result: cardOne } = renderHook(() => usePrinterTagsFromFleet('printer-1'), {
      wrapper: wrapper(qc),
    });
    const { result: cardTwo } = renderHook(() => usePrinterTagsFromFleet('printer-2'), {
      wrapper: wrapper(qc),
    });

    await waitFor(() => expect(cardOne.current.data).toBeDefined());
    await waitFor(() => expect(cardTwo.current.data).toBeDefined());

    expect(hoisted.getObjectsTags).toHaveBeenCalledTimes(1);
    expect(cardOne.current.data).toEqual([{ id: 'tag-1', name: 'Production' }]);
    expect(cardTwo.current.data).toEqual([]);
  });

  it('uses a stable, prefixed query key so invalidation callers can target it directly', () => {
    expect(printerTagsFleetQueryKey).toEqual(['printer-tags', 'fleet']);
  });
});