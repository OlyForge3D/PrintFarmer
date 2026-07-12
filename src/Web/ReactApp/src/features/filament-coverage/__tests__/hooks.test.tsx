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
  usePrinterFilamentCoverage,
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
});

// Silence React 19 act warnings from empty renders
void render;
