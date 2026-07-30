import "@testing-library/jest-dom";
import React from "react";
import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import {
  FilamentCoverageBadge,
  RunoutRiskChip,
  ToolheadCoverageRow,
  PrinterCoverageSummary,
} from "../components/FilamentCoverageBadge";
import type { ToolheadCoverage, PrinterFilamentCoverage } from "../types";

describe("FilamentCoverageBadge", () => {
  it("renders the covers state with an accessible name and icon", () => {
    render(<FilamentCoverageBadge status="covers" ariaContext="Alpha" />);
    const badge = screen.getByRole("status");
    expect(badge).toHaveTextContent(/Filament OK/i);
    expect(badge).toHaveAttribute("aria-label", "Filament OK — Alpha");
    expect(badge).toHaveAttribute("data-status", "covers");
  });

  it("renders the runout state distinctly (icon + label, not color alone)", () => {
    render(<FilamentCoverageBadge status="runout" reason="insufficient-remaining" />);
    const badge = screen.getByRole("status");
    expect(badge).toHaveTextContent(/Runout risk/i);
    expect(badge).toHaveAttribute("title", "insufficient-remaining");
    expect(badge).toHaveAttribute("data-status", "runout");
  });

  it("renders the unknown state without claiming runout", () => {
    render(<FilamentCoverageBadge status="unknown" />);
    const badge = screen.getByRole("status");
    expect(badge).toHaveTextContent(/Filament unknown/i);
    expect(badge).not.toHaveTextContent(/runout/i);
    expect(badge).toHaveAttribute("data-status", "unknown");
  });

  it("keeps the accessible name in compact mode", () => {
    render(<FilamentCoverageBadge status="runout" compact ariaContext="Alpha" />);
    const badge = screen.getByRole("status");
    expect(badge).toHaveAttribute("aria-label", "Runout risk — Alpha");
    // sr-only label preserves the readable name
    expect(badge.textContent).toMatch(/Runout risk/i);
  });
});

describe("RunoutRiskChip", () => {
  it("renders nothing when there is no prediction", () => {
    const { container } = render(<RunoutRiskChip predictedRunoutAt={null} />);
    expect(container.firstChild).toBeNull();
  });

  it("shows a relative window when a runout time is known", () => {
    const future = new Date(Date.now() + 90 * 60_000).toISOString();
    render(<RunoutRiskChip predictedRunoutAt={future} />);
    expect(screen.getByRole("status")).toHaveTextContent(/Runs out in/i);
  });

  it("falls back to layer when only the layer is known", () => {
    render(<RunoutRiskChip predictedRunoutAt={null} predictedRunoutLayer={200} />);
    expect(screen.getByRole("status")).toHaveTextContent(/layer 200/i);
  });
});

describe("ToolheadCoverageRow", () => {
  const toolhead: ToolheadCoverage = {
    toolheadIndex: 1,
    toolheadName: "Extruder 2",
    spoolId: 42,
    material: "PETG",
    filamentColor: "#0f0",
    remainingGrams: 350,
    currentJobRequiredGrams: 800,
    currentJobRemainingGrams: 500,
    queuedRequiredGrams: null,
    totalDemandGrams: null,
    status: "runout",
    statusReason: "insufficient-remaining",
    predictedRunoutAt: new Date(Date.now() + 30 * 60_000).toISOString(),
    predictedRunoutLayer: 123,
  };

  it("renders the toolhead name, badge, and runout chip together", () => {
    render(<ToolheadCoverageRow toolhead={toolhead} />);
    expect(screen.getByText("Extruder 2")).toBeInTheDocument();
    const statuses = screen.getAllByRole("status");
    // one for the badge, one for the runout chip
    expect(statuses.length).toBeGreaterThanOrEqual(2);
    expect(statuses.some((el) => /Runout risk/i.test(el.textContent ?? ""))).toBe(true);
    expect(statuses.some((el) => /Runs out in/i.test(el.textContent ?? ""))).toBe(true);
  });

  it("shows 'demand unknown' when the total demand is null", () => {
    render(<ToolheadCoverageRow toolhead={toolhead} />);
    expect(screen.getByText(/demand unknown/i)).toBeInTheDocument();
  });
});

describe("PrinterCoverageSummary", () => {
  const coverage: PrinterFilamentCoverage = {
    printerId: "p-1",
    printerName: "Alpha",
    status: "runout",
    toolheads: [],
    activeJobId: null,
    activeJobName: null,
    activeJobProgress: null,
    earliestPredictedRunoutAt: new Date(Date.now() + 20 * 60_000).toISOString(),
    assignedQueuedJobCount: 0,
    evaluatedAtUtc: "2025-01-01T00:00:00Z",
  };

  it("renders nothing when coverage is not available", () => {
    const { container } = render(<PrinterCoverageSummary coverage={null} />);
    expect(container.firstChild).toBeNull();
  });

  it("shows both the badge and the runout chip when at risk", () => {
    render(<PrinterCoverageSummary coverage={coverage} />);
    const statuses = screen.getAllByRole("status");
    expect(statuses.some((el) => /Runout risk/i.test(el.textContent ?? ""))).toBe(true);
    expect(statuses.some((el) => /Runs out in/i.test(el.textContent ?? ""))).toBe(true);
  });

  it("omits the runout chip when the status is 'covers'", () => {
    render(
      <PrinterCoverageSummary
        coverage={{ ...coverage, status: "covers", earliestPredictedRunoutAt: null }}
      />,
    );
    const statuses = screen.getAllByRole("status");
    expect(statuses.some((el) => /Filament OK/i.test(el.textContent ?? ""))).toBe(true);
    expect(statuses.some((el) => /Runs out in/i.test(el.textContent ?? ""))).toBe(false);
  });
});
