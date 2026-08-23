import '@testing-library/jest-dom';
import React from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const hoisted = vi.hoisted(() => ({
  getCalibrationCandidates: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: { getCalibrationCandidates: hoisted.getCalibrationCandidates },
}));

import {
  useFleetCalibrationCandidates,
  useCalibrationCandidateFromFleet,
  calibrationCandidatesFleetQueryKey,
} from '../useCalibrationCandidatesFleet';

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

describe('useCalibrationCandidatesFleet (#1923)', () => {
  beforeEach(() => {
    hoisted.getCalibrationCandidates.mockReset();
  });

  it('requests the batched fleet endpoint once', async () => {
    hoisted.getCalibrationCandidates.mockResolvedValueOnce([
      { id: 'printer-1', name: 'Printer One', eligible: true, missingInputs: [], rejectionReasons: [], firmware: {}, toolheads: [] },
    ]);
    const qc = makeClient();

    const { result } = renderHook(() => useFleetCalibrationCandidates(), { wrapper: wrapper(qc) });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(hoisted.getCalibrationCandidates).toHaveBeenCalledTimes(1);
    expect(hoisted.getCalibrationCandidates).toHaveBeenCalledWith(expect.anything());
  });

  it("selects only the requested printer's candidate from the shared fleet cache", async () => {
    hoisted.getCalibrationCandidates.mockResolvedValueOnce([
      { id: 'printer-1', name: 'Printer One', eligible: true, missingInputs: [], rejectionReasons: [], firmware: {}, toolheads: [] },
      { id: 'printer-2', name: 'Printer Two', eligible: false, missingInputs: ['firmware'], rejectionReasons: [], firmware: {}, toolheads: [] },
    ]);
    const qc = makeClient();

    const { result } = renderHook(() => useCalibrationCandidateFromFleet('printer-2'), {
      wrapper: wrapper(qc),
    });

    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(result.current.data?.id).toBe('printer-2');
    expect(result.current.data?.eligible).toBe(false);
  });

  it('returns undefined for a printer absent from the fleet response', async () => {
    hoisted.getCalibrationCandidates.mockResolvedValueOnce([
      { id: 'printer-1', name: 'Printer One', eligible: true, missingInputs: [], rejectionReasons: [], firmware: {}, toolheads: [] },
    ]);
    const qc = makeClient();

    const { result } = renderHook(() => useCalibrationCandidateFromFleet('printer-missing'), {
      wrapper: wrapper(qc),
    });

    await waitFor(() => expect(result.current.isPending).toBe(false));
    expect(result.current.data).toBeUndefined();
  });

  it('shares one fleet request across multiple concurrent per-printer selectors', async () => {
    hoisted.getCalibrationCandidates.mockResolvedValueOnce([
      { id: 'printer-1', name: 'Printer One', eligible: false, missingInputs: [], rejectionReasons: [], firmware: {}, toolheads: [] },
    ]);
    const qc = makeClient();

    const { result: cardOne } = renderHook(() => useCalibrationCandidateFromFleet('printer-1'), {
      wrapper: wrapper(qc),
    });
    const { result: cardTwo } = renderHook(() => useCalibrationCandidateFromFleet('printer-2'), {
      wrapper: wrapper(qc),
    });

    await waitFor(() => expect(cardOne.current.data).toBeDefined());
    await waitFor(() => expect(cardTwo.current.isPending).toBe(false));

    expect(hoisted.getCalibrationCandidates).toHaveBeenCalledTimes(1);
    expect(cardOne.current.data?.id).toBe('printer-1');
    expect(cardTwo.current.data).toBeUndefined();
  });

  it('uses a stable, prefixed query key so consumers can prime/invalidate it directly', () => {
    expect(calibrationCandidatesFleetQueryKey).toEqual(['calibration-candidates', 'fleet']);
  });
});
