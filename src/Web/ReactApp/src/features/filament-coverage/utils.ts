import type { PrinterFilamentCoverage } from './types';

/** Coverage is meaningful only for an active print or assigned queued work. */
export function hasFilamentCoverageDemand(
  coverage: PrinterFilamentCoverage | null | undefined,
): coverage is PrinterFilamentCoverage {
  return !!coverage && (
    (typeof coverage.activeJobId === 'string' && coverage.activeJobId.trim().length > 0)
    || (Number.isFinite(coverage.assignedQueuedJobCount) && coverage.assignedQueuedJobCount > 0)
  );
}

/**
 * Hides snapshots without confirmed print demand, then downgrades coverage
 * to "unknown" for both the printer-level
 * status and every toolhead when the printer is offline. An offline
 * printer's last-known coverage can't be verified (e.g. a spool could have
 * been pulled while unreachable), so it must never render a "covers"
 * (green "Filament OK") or "runout" indicator based on stale data
 * (issue #1684). Numeric fields (remaining/demand grams) are preserved
 * since they're informational, not a claimed live health signal.
 *
 * Shared by every surface that renders filament coverage for a printer
 * (MaterialLoadout, FilamentCoverageBreakdown, PrinterCoverageSummary) so
 * the offline gating logic lives in exactly one place.
 */
export function withOfflineOverride(
  coverage: PrinterFilamentCoverage | null | undefined,
  isOnline: boolean,
): PrinterFilamentCoverage | null | undefined {
  if (!hasFilamentCoverageDemand(coverage)) return null;
  if (isOnline) return coverage;
  return {
    ...coverage,
    status: 'unknown',
    toolheads: coverage.toolheads.map((th) => ({ ...th, status: 'unknown' })),
  };
}
