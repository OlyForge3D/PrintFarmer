import "@testing-library/jest-dom";
import React from "react";
import { act, render, renderHook, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

const hoisted = vi.hoisted(() => {
  return {
    apiGet: vi.fn(),
    onFilamentCoverageChangedCb: { current: null as null | ((event: unknown) => void) },
    signalRMock: {
      connect: vi.fn().mockResolvedValue(undefined),
      onFilamentCoverageChanged: vi.fn(),
    },
  };
});

vi.mock("@/services/api", () => ({
  apiClient: { get: hoisted.apiGet },
}));

vi.mock("@/services/printer-signalr", () => {
  hoisted.signalRMock.onFilamentCoverageChanged = vi.fn(
    (cb: (event: unknown) => void) => {
      hoisted.onFilamentCoverageChangedCb.current = cb;
      return () => {
        hoisted.onFilamentCoverageChangedCb.current = null;
      };
    },
  );
  return { printerSignalRService: hoisted.signalRMock };
});

import {
  __resetFilamentCoverageSubscriptionForTests,
  filamentCoverageQueryKeys,
  FLEET_INVALIDATE_THROTTLE_MS,
  usePrinterFilamentCoverage,
  usePrinterCoverageFromFleet,
  useFleetFilamentCoverage,
} from "../hooks";

const mockGet = hoisted.apiGet;

function wrapper(client: QueryClient) {
  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

function makeClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        refetchOnWindowFocus: false,
        refetchInterval: false,
        gcTime: 0,
      },
    },
  });
}

describe("filament coverage hooks", () => {
  beforeEach(() => {
    mockGet.mockReset();
    hoisted.signalRMock.connect.mockClear();
    hoisted.signalRMock.onFilamentCoverageChanged.mockClear();
    __resetFilamentCoverageSubscriptionForTests();
    hoisted.onFilamentCoverageChangedCb.current = null;
  });

  afterEach(() => {
    __resetFilamentCoverageSubscriptionForTests();
  });

  it("fetches fleet coverage and returns decoded data", async () => {
    mockGet.mockResolvedValueOnce({
      data: {
        printers: [
          { printerId: "p-1", printerName: "Alpha", status: "covers", toolheads: [] },
        ],
        evaluatedAtUtc: "2025-01-01T00:00:00Z",
      },
    });
    const qc = makeClient();
    const { result } = renderHook(() => useFleetFilamentCoverage(), {
      wrapper: wrapper(qc),
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.printers[0].status).toBe("covers");
  });

  it("returns null when the fleet endpoint 404s (feature disabled)", async () => {
    mockGet.mockRejectedValueOnce({ response: { status: 404 } });
    const qc = makeClient();
    const { result } = renderHook(() => useFleetFilamentCoverage(), {
      wrapper: wrapper(qc),
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBeNull();
  });

  it("does not subscribe to SignalR when the fleet query is disabled", () => {
    const qc = makeClient();
    renderHook(() => useFleetFilamentCoverage({ enabled: false }), {
      wrapper: wrapper(qc),
    });

    expect(hoisted.signalRMock.onFilamentCoverageChanged).not.toHaveBeenCalled();
    expect(hoisted.signalRMock.connect).not.toHaveBeenCalled();
  });

  it("deduplicates concurrent fleet selectors and decodes each printer", async () => {
    const fleetResponse = {
      data: {
        printers: [
          { printerId: "p-1", printerName: "Alpha", status: "covers", toolheads: [] },
          { printerId: "p-2", printerName: "Beta", status: "Runout", toolheads: [] },
          { printerId: "p-3", printerName: "Gamma", status: "Unknown", toolheads: [] },
        ],
        evaluatedAtUtc: "2025-01-01T00:00:00Z",
      },
    };
    let resolveFleet!: (value: typeof fleetResponse) => void;
    mockGet.mockReturnValueOnce(
      new Promise<typeof fleetResponse>((resolve) => {
        resolveFleet = resolve;
      }),
    );
    const qc = makeClient();
    const { result } = renderHook(
      () => ({
        fleet: useFleetFilamentCoverage(),
        first: usePrinterCoverageFromFleet("p-1"),
        second: usePrinterCoverageFromFleet("p-2"),
        third: usePrinterCoverageFromFleet("p-3"),
      }),
      { wrapper: wrapper(qc) },
    );

    await waitFor(() => expect(mockGet).toHaveBeenCalledTimes(1));
    expect(mockGet).toHaveBeenCalledWith("/printers/filament-coverage", {
      signal: expect.any(AbortSignal),
    });

    act(() => resolveFleet(fleetResponse));

    await waitFor(() => expect(result.current.fleet.isSuccess).toBe(true));
    expect(result.current.first.data).toMatchObject({
      printerId: "p-1",
      status: "covers",
    });
    expect(result.current.second.data).toMatchObject({
      printerId: "p-2",
      status: "runout",
    });
    expect(result.current.third.data).toMatchObject({
      printerId: "p-3",
      status: "unknown",
    });
    expect(mockGet).toHaveBeenCalledTimes(1);
  });

  it("derives per-printer coverage from the fleet snapshot without extra requests", async () => {
    mockGet.mockResolvedValueOnce({
      data: {
        printers: [
          { printerId: "p-1", printerName: "Alpha", status: "covers", toolheads: [] },
        ],
        evaluatedAtUtc: "2025-01-01T00:00:00Z",
      },
    });
    const qc = makeClient();
    const { result: fleetResult } = renderHook(() => useFleetFilamentCoverage(), {
      wrapper: wrapper(qc),
    });
    await waitFor(() => expect(fleetResult.current.isSuccess).toBe(true));

    const { result: printerResult } = renderHook(
      () => usePrinterFilamentCoverage("p-1"),
      { wrapper: wrapper(qc) },
    );
    await waitFor(() => expect(printerResult.current.isSuccess).toBe(true));
    expect(printerResult.current.data?.printerId).toBe("p-1");
    // only the fleet call fired — no per-printer fetch when the fleet cache covered it
    expect(mockGet).toHaveBeenCalledTimes(1);
  });

  it("invalidates fleet + specific printer queries on filamentcoveragechanged", async () => {
    mockGet.mockResolvedValue({
      data: {
        printers: [
          { printerId: "p-1", printerName: "Alpha", status: "covers", toolheads: [] },
        ],
        evaluatedAtUtc: "2025-01-01T00:00:00Z",
      },
    });
    const qc = makeClient();
    const invalidateSpy = vi.spyOn(qc, "invalidateQueries");

    renderHook(() => useFleetFilamentCoverage(), { wrapper: wrapper(qc) });
    await waitFor(() =>
      expect(hoisted.signalRMock.onFilamentCoverageChanged).toHaveBeenCalled(),
    );

    // Emit a per-printer event
    act(() => {
      hoisted.onFilamentCoverageChangedCb.current?.({
        printerId: "p-1",
        reason: "spoolBinding",
        occurredAt: "2025-01-01T00:00:00Z",
      });
    });

    await waitFor(() => {
      const calls = invalidateSpy.mock.calls.map((c) => c[0]);
      expect(
        calls.some((k) =>
          Array.isArray(k?.queryKey) &&
            JSON.stringify(k.queryKey) === JSON.stringify(filamentCoverageQueryKeys.fleet()),
        ),
      ).toBe(true);
      expect(
        calls.some((k) =>
          Array.isArray(k?.queryKey) &&
            JSON.stringify(k.queryKey) ===
              JSON.stringify(filamentCoverageQueryKeys.printer("p-1")),
        ),
      ).toBe(true);
    });
  });

  it("invalidates all per-printer subscriptions on a fleet-wide event", async () => {
    mockGet.mockResolvedValue({
      data: {
        printers: [
          { printerId: "p-1", printerName: "Alpha", status: "covers", toolheads: [] },
        ],
        evaluatedAtUtc: "2025-01-01T00:00:00Z",
      },
    });
    const qc = makeClient();
    const invalidateSpy = vi.spyOn(qc, "invalidateQueries");

    renderHook(() => useFleetFilamentCoverage(), { wrapper: wrapper(qc) });
    await waitFor(() =>
      expect(hoisted.signalRMock.onFilamentCoverageChanged).toHaveBeenCalled(),
    );

    act(() => {
      hoisted.onFilamentCoverageChangedCb.current?.({
        printerId: null,
        reason: "thresholdChanged",
        occurredAt: "2025-01-01T00:00:00Z",
      });
    });

    await waitFor(() => {
      const keys = invalidateSpy.mock.calls.map((c) => JSON.stringify(c[0]?.queryKey));
      expect(keys).toContain(JSON.stringify(["filament-coverage", "printer"]));
    });
  });

  it("throttles fleet invalidation across a burst of per-printer ticks: one leading call, then exactly one trailing call after the window", async () => {
    mockGet.mockResolvedValue({
      data: { printers: [], evaluatedAtUtc: "2025-01-01T00:00:00Z" },
    });
    const qc = makeClient();
    const invalidateSpy = vi.spyOn(qc, "invalidateQueries");
    const fleetKey = JSON.stringify(filamentCoverageQueryKeys.fleet());
    const fleetCallCount = () =>
      invalidateSpy.mock.calls.filter(
        (c) => JSON.stringify(c[0]?.queryKey) === fleetKey,
      ).length;

    renderHook(() => useFleetFilamentCoverage(), { wrapper: wrapper(qc) });
    await waitFor(() =>
      expect(hoisted.signalRMock.onFilamentCoverageChanged).toHaveBeenCalled(),
    );

    const emit = (printerId: string) => {
      act(() => {
        hoisted.onFilamentCoverageChangedCb.current?.({
          printerId,
          reason: "jobProgress",
          occurredAt: "2025-01-01T00:00:00Z",
        });
      });
    };

    // Leading edge: the first tick in the burst invalidates the fleet cache
    // immediately, so a lone event is never delayed.
    emit("p-1");
    await waitFor(() => expect(fleetCallCount()).toBe(1));

    // Further ticks arriving within the throttle window are coalesced —
    // no extra fleet invalidation fires yet, even though each printer's own
    // slice is still invalidated immediately.
    emit("p-2");
    emit("p-3");
    expect(fleetCallCount()).toBe(1);
    expect(invalidateSpy.mock.calls.some(
      (c) => JSON.stringify(c[0]?.queryKey) === JSON.stringify(filamentCoverageQueryKeys.printer("p-2")),
    )).toBe(true);
    expect(invalidateSpy.mock.calls.some(
      (c) => JSON.stringify(c[0]?.queryKey) === JSON.stringify(filamentCoverageQueryKeys.printer("p-3")),
    )).toBe(true);

    // Once the throttle window elapses, exactly one trailing invalidation
    // fires — fleet coverage catches up shortly after the burst settles
    // instead of staying stale indefinitely.
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, FLEET_INVALIDATE_THROTTLE_MS + 50));
    });
    await waitFor(() => expect(fleetCallCount()).toBe(2));
  });

  it("does NOT unsubscribe SignalR when only one of two hooks sharing the same QueryClient unmounts", async () => {
    mockGet.mockResolvedValue({
      data: { printers: [], evaluatedAtUtc: "2025-01-01T00:00:00Z" },
    });
    const qc = makeClient();

    // Mount two hooks that share the same singleton QueryClient
    const { unmount: unmount1 } = renderHook(() => useFleetFilamentCoverage(), {
      wrapper: wrapper(qc),
    });
    const { unmount: unmount2 } = renderHook(() => useFleetFilamentCoverage(), {
      wrapper: wrapper(qc),
    });

    await waitFor(() =>
      expect(hoisted.signalRMock.onFilamentCoverageChanged).toHaveBeenCalled(),
    );

    // Store the original unsubscribe spy so we can verify it is NOT called yet
    const unsubscribeSpy = vi.fn();
    // Patch the callback ref that was captured by onFilamentCoverageChanged
    // Instead, we verify via onFilamentCoverageChangedCb: after unmount1 the
    // callback must still be live (not null).
    const cbBeforeUnmount = hoisted.onFilamentCoverageChangedCb.current;
    expect(cbBeforeUnmount).not.toBeNull();

    // Unmount the FIRST hook — subscription must remain because the second is still mounted
    act(() => { unmount1(); });

    // The SignalR callback must still be registered (not nulled by the unsubscribe return value)
    expect(hoisted.onFilamentCoverageChangedCb.current).not.toBeNull();

    // An event fired now must still cause invalidation (subscription is live)
    const invalidateSpy = vi.spyOn(qc, "invalidateQueries");
    act(() => {
      hoisted.onFilamentCoverageChangedCb.current?.({
        printerId: "p-1",
        reason: "spoolBinding",
        occurredAt: "2025-01-01T00:00:00Z",
      });
    });

    await waitFor(() => expect(invalidateSpy).toHaveBeenCalled());

    // Clean up the second hook
    act(() => { unmount2(); });
    // Now the subscription should be torn down
    expect(hoisted.onFilamentCoverageChangedCb.current).toBeNull();

    unsubscribeSpy.mockRestore?.();
  });

  it("falls through to per-printer API when fleet cache is stale", async () => {
    // Arrange: prime the fleet cache successfully
    const fleetPayload = {
      data: {
        printers: [
          { printerId: "p-1", printerName: "Alpha", status: "covers", toolheads: [] },
        ],
        evaluatedAtUtc: "2025-01-01T00:00:00Z",
      },
    };
    mockGet.mockResolvedValueOnce(fleetPayload); // fleet call
    const printerPayload = {
      data: {
        printerId: "p-1",
        printerName: "Alpha",
        status: "runout",
        toolheads: [],
        activeJobId: null,
        activeJobName: null,
        activeJobProgress: null,
        earliestPredictedRunoutAt: null,
        assignedQueuedJobCount: 0,
        evaluatedAtUtc: "2025-01-01T01:00:00Z",
      },
    };
    mockGet.mockResolvedValueOnce(printerPayload); // per-printer call after cache expires

    const qc = makeClient();

    // Prime fleet cache
    const { result: fleetResult } = renderHook(() => useFleetFilamentCoverage(), {
      wrapper: wrapper(qc),
    });
    await waitFor(() => expect(fleetResult.current.isSuccess).toBe(true));

    // Age the fleet snapshot beyond the hook's stale window.
    act(() => {
      qc.setQueryData(
        filamentCoverageQueryKeys.fleet(),
        fleetResult.current.data,
        { updatedAt: Date.now() - 15_001 },
      );
    });

    // The per-printer hook must not derive from an expired fleet snapshot.
    const { result: printerResult } = renderHook(
      () => usePrinterFilamentCoverage("p-1"),
      { wrapper: wrapper(qc) },
    );
    await waitFor(() => expect(printerResult.current.isSuccess).toBe(true));

    // The per-printer service must have been called (2 total: fleet + per-printer)
    expect(mockGet).toHaveBeenCalledTimes(2);
    // And the result should be from the fresh per-printer response, not the stale fleet entry
    expect(printerResult.current.data?.status).toBe("runout");
  });
});

// Silence React 19 act warnings from empty renders
void render;
