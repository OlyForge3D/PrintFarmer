/**
 * Detailed per-printer filament coverage breakdown for the details sidebar
 * (issue #717). Renders the aggregate badge, an "unknown"/"loading" state
 * where appropriate, and one `ToolheadCoverageRow` per toolhead slot.
 * Rendering is skipped entirely when the feature is disabled (the hook
 * returns `null`) so callers can drop it into any spool section without
 * adding a placeholder.
 */
import React from "react";
import { usePrinterCoverageFromFleet } from "../hooks";
import { withOfflineOverride } from "../utils";
import {
  FilamentCoverageBadge,
  RunoutRiskChip,
  ToolheadCoverageRow,
} from "./FilamentCoverageBadge";

export interface FilamentCoverageBreakdownProps {
  printerId: string;
  className?: string;
  /**
   * Whether the printer is currently reachable. Defaults to `true` for
   * back-compat. When `false`, the last-known coverage snapshot cannot be
   * verified (issue #1684), so both the aggregate status and every
   * toolhead's status are downgraded to "unknown" — this panel must never
   * show a stale "Filament OK"/"Runout risk" claim for an offline printer.
   */
  isOnline?: boolean;
}

export function FilamentCoverageBreakdown({
  printerId,
  className,
  isOnline = true,
}: FilamentCoverageBreakdownProps): React.ReactElement | null {
  const { data: rawCoverage, isPending, isError } =
    usePrinterCoverageFromFleet(printerId);
  const coverage = withOfflineOverride(rawCoverage, isOnline);

  if (isPending) {
    return null;
  }

  if (isError) {
    return null;
  }

  // Feature disabled — the sidebar's existing spool card still renders
  // the raw remaining-weight info, so we just render nothing here.
  if (coverage == null) return null;

  return (
    <div
      className={[
        "mb-2 flex flex-col gap-1.5 rounded-sm border border-pf-border/60 bg-pf-bg-0/40 p-2",
        className ?? "",
      ]
        .join(" ")
        .trim()}
      data-testid="filament-coverage-breakdown"
    >
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-1.5">
          <FilamentCoverageBadge
            status={coverage.status}
            ariaContext={coverage.printerName || undefined}
          />
          {coverage.status === "runout" && (
            <RunoutRiskChip
              predictedRunoutAt={coverage.earliestPredictedRunoutAt}
              predictedRunoutLayer={null}
            />
          )}
        </div>
        {coverage.assignedQueuedJobCount > 0 && (
          <span className="text-[11px] text-pf-text-tertiary">
            +{coverage.assignedQueuedJobCount} queued
          </span>
        )}
      </div>
      {coverage.toolheads.length > 0 && (
        <div className="flex flex-col gap-1">
          {coverage.toolheads.map((th) => (
            <ToolheadCoverageRow key={th.toolheadIndex} toolhead={th} />
          ))}
        </div>
      )}
    </div>
  );
}
