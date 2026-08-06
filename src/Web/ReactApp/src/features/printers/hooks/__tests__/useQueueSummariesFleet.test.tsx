import '@testing-library/jest-dom';
import React from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const hoisted = vi.hoisted(() => ({
  getPrinterQueueSummaries: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: { getPrinterQueueSummaries: hoisted.getPrinterQueueSummaries },
}));

import {
  useFleetQueueSummaries,
  useQueueSummaryFromFleet,
  queueSummariesFleetQueryKey,
} from '../useQueueSummariesFleet';

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

describe('useQueueSummariesFleet (#1146 item 9)', () => {
  beforeEach(() => {
    hoisted.getPrinterQueueSummaries.mockReset();
  });

  it('requests the batched fleet endpoint once', async () => {
    hoisted.getPrinterQueueSummaries.mockResolvedValueOnce([
      { printerId: 'printer-1', queuedCount: 1, printingCount: 1, printingPosition: 1 },
    ]);
    const qc = makeClient();

    const { result } = renderHook(() => useFleetQueueSummaries(), { wrapper: wrapper(qc) });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(hoisted.getPrinterQueueSummaries).toHaveBeenCalledTimes(1);
    expect(hoisted.getPrinterQueueSummaries).toHaveBeenCalledWith(expect.anything());
  });

  it("selects only the requested printer's summary from the shared fleet cache", async () => {
    hoisted.getPrinterQueueSummaries.mockResolvedValueOnce([
      { printerId: 'printer-1', queuedCount: 1, printingCount: 1, printingPosition: 1 },
      { printerId: 'printer-2', queuedCount: 0, printingCount: 1, printingPosition: 1 },
    ]);
    const qc = makeClient();

    const { result } = renderHook(() => useQueueSummaryFromFleet('printer-2'), {
      wrapper: wrapper(qc),
    });

    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(result.current.data).toEqual({
      printerId: 'printer-2',
      queuedCount: 0,
      printingCount: 1,
      printingPosition: 1,
    });
  });

  it('returns undefined for an idle printer that is absent from the fleet response (no active job)', async () => {
    hoisted.getPrinterQueueSummaries.mockResolvedValueOnce([
      { printerId: 'printer-1', queuedCount: 2, printingCount: 1, printingPosition: 1 },
    ]);
    const qc = makeClient();

    const { result } = renderHook(() => useQueueSummaryFromFleet('printer-idle'), {
      wrapper: wrapper(qc),
    });

    await waitFor(() => expect(result.current.isPending).toBe(false));
    expect(result.current.data).toBeUndefined();
  });

  it('shares one fleet request across multiple concurrent per-printer selectors', async () => {
    hoisted.getPrinterQueueSummaries.mockResolvedValueOnce([
      { printerId: 'printer-1', queuedCount: 1, printingCount: 1, printingPosition: 1 },
    ]);
    const qc = makeClient();

    const { result: cardOne } = renderHook(() => useQueueSummaryFromFleet('printer-1'), {
      wrapper: wrapper(qc),
    });
    const { result: cardTwo } = renderHook(() => useQueueSummaryFromFleet('printer-2'), {
      wrapper: wrapper(qc),
    });

    await waitFor(() => expect(cardOne.current.data).toBeDefined());
    await waitFor(() => expect(cardTwo.current.isPending).toBe(false));

    expect(hoisted.getPrinterQueueSummaries).toHaveBeenCalledTimes(1);
    expect(cardOne.current.data?.printerId).toBe('printer-1');
    expect(cardTwo.current.data).toBeUndefined();
  });

  it('uses a stable, prefixed query key so consumers can prime/invalidate it directly', () => {
    expect(queueSummariesFleetQueryKey).toEqual(['queue-summaries', 'fleet']);
  });
});