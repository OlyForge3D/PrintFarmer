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
});
