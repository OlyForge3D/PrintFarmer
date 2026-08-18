/**
 * Tests for FilamentCoverageBreakdown (issue #717).
 * Loading and error branches must render nothing so the legacy spool display
 * is not disrupted by dead-space text.
 */
import "@testing-library/jest-dom";
import React from "react";
import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { UseQueryResult } from "@tanstack/react-query";
import type { PrinterFilamentCoverage } from "../types";

// ---------------------------------------------------------------------------
// Mock the hook so we can drive isLoading / isError / data independently
// ---------------------------------------------------------------------------

const mockUsePrinterCoverageFromFleet = vi.fn<
  [],
  Partial<UseQueryResult<PrinterFilamentCoverage | null>>
>();

vi.mock("../hooks", () => ({
  usePrinterCoverageFromFleet: () => mockUsePrinterCoverageFromFleet(),
  useFleetFilamentCoverage: vi.fn(() => ({ data: null, isLoading: false })),
}));

import { FilamentCoverageBreakdown } from "../components/FilamentCoverageBreakdown";

function wrapper(qc: QueryClient) {
  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  };
}

function makeClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
}

const baseCoverage: PrinterFilamentCoverage = {
  printerId: "p-1",
  printerName: "Alpha",
  status: "covers",
  toolheads: [],
  activeJobId: null,
  activeJobName: null,
  activeJobProgress: null,
  earliestPredictedRunoutAt: null,
  assignedQueuedJobCount: 0,
  evaluatedAtUtc: "2025-01-01T00:00:00Z",
};

describe("FilamentCoverageBreakdown", () => {
  it("renders nothing while loading (isPending = true)", () => {
    mockUsePrinterCoverageFromFleet.mockReturnValue({
      isPending: true,
      isError: false,
      data: undefined,
    });
    const qc = makeClient();
    const { container } = render(
      <FilamentCoverageBreakdown printerId="p-1" />,
      { wrapper: wrapper(qc) },
    );
    expect(container.firstChild).toBeNull();
  });

  it("renders nothing on error (isError = true)", () => {
    mockUsePrinterCoverageFromFleet.mockReturnValue({
      isPending: false,
      isError: true,
      data: undefined,
    });
    const qc = makeClient();
    const { container } = render(
      <FilamentCoverageBreakdown printerId="p-1" />,
      { wrapper: wrapper(qc) },
    );
    expect(container.firstChild).toBeNull();
  });

  it("renders nothing when coverage is null (feature disabled)", () => {
    mockUsePrinterCoverageFromFleet.mockReturnValue({
      isPending: false,
      isError: false,
      data: null,
    });
    const qc = makeClient();
    const { container } = render(
      <FilamentCoverageBreakdown printerId="p-1" />,
      { wrapper: wrapper(qc) },
    );
    expect(container.firstChild).toBeNull();
  });

  it("renders the breakdown panel when coverage data is present", () => {
    mockUsePrinterCoverageFromFleet.mockReturnValue({
      isPending: false,
      isError: false,
      data: baseCoverage,
    });
    const qc = makeClient();
    render(<FilamentCoverageBreakdown printerId="p-1" />, {
      wrapper: wrapper(qc),
    });
    expect(
      screen.getByTestId("filament-coverage-breakdown"),
    ).toBeInTheDocument();
  });

  // Regression tests for issue #1684: this panel is mounted directly
  // alongside MaterialLoadout in both DetailedPrinterCard's expanded view
  // and PrinterDetailsSidebar's drill-down, and independently fetched the
  // same fleet-wide coverage cache with no isOnline awareness — so it
  // reproduced the exact same false "Filament OK"/"Runout risk" claim for
  // an offline printer, one scroll position away from the fix applied to
  // MaterialLoadout and PrinterCoverageSummary.
  it("downgrades to unknown and hides 'Filament OK' when isOnline=false (#1684)", () => {
    mockUsePrinterCoverageFromFleet.mockReturnValue({
      isPending: false,
      isError: false,
      data: baseCoverage,
    });
    const qc = makeClient();
    render(<FilamentCoverageBreakdown printerId="p-1" isOnline={false} />, {
      wrapper: wrapper(qc),
    });
    expect(screen.getByText("Filament unknown")).toBeInTheDocument();
    expect(screen.queryByText("Filament OK")).not.toBeInTheDocument();
  });

  it("suppresses the runout chip when offline even for a last-known runout status (#1684)", () => {
    mockUsePrinterCoverageFromFleet.mockReturnValue({
      isPending: false,
      isError: false,
      data: {
        ...baseCoverage,
        status: "runout",
        earliestPredictedRunoutAt: new Date(Date.now() + 20 * 60_000).toISOString(),
      },
    });
    const qc = makeClient();
    render(<FilamentCoverageBreakdown printerId="p-1" isOnline={false} />, {
      wrapper: wrapper(qc),
    });
    expect(screen.queryByText("Runout risk")).not.toBeInTheDocument();
    expect(screen.getByText("Filament unknown")).toBeInTheDocument();
  });

  it("keeps existing (online) badge behavior when isOnline is not provided (back-compat)", () => {
    mockUsePrinterCoverageFromFleet.mockReturnValue({
      isPending: false,
      isError: false,
      data: baseCoverage,
    });
    const qc = makeClient();
    render(<FilamentCoverageBreakdown printerId="p-1" />, {
      wrapper: wrapper(qc),
    });
    expect(screen.getByText("Filament OK")).toBeInTheDocument();
  });

  it("suppresses a per-toolhead runout chip when offline, even with a stale predicted runout time (#1684)", () => {
    mockUsePrinterCoverageFromFleet.mockReturnValue({
      isPending: false,
      isError: false,
      data: {
        ...baseCoverage,
        status: "runout",
        toolheads: [
          {
            toolheadIndex: 1,
            toolheadName: "Extruder 1",
            spoolId: 42,
            material: "PETG",
            filamentColor: "#0f0",
            remainingGrams: 100,
            currentJobRequiredGrams: 400,
            currentJobRemainingGrams: 300,
            queuedRequiredGrams: null,
            totalDemandGrams: 400,
            status: "runout",
            statusReason: "insufficient-remaining",
            predictedRunoutAt: new Date(Date.now() + 20 * 60_000).toISOString(),
            predictedRunoutLayer: 123,
          },
        ],
      },
    });
    const qc = makeClient();
    render(<FilamentCoverageBreakdown printerId="p-1" isOnline={false} />, {
      wrapper: wrapper(qc),
    });
    expect(screen.queryByText(/Runs out in/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Runout risk/i)).not.toBeInTheDocument();
    // The per-toolhead badge must also read "unknown", not "Filament OK"/"Runout risk".
    const badges = screen.getAllByRole("status");
    expect(badges.some((el) => /Filament unknown/i.test(el.textContent ?? ""))).toBe(true);
  });
});
