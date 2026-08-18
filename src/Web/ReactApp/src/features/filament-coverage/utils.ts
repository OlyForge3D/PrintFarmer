import type { PrinterFilamentCoverage } from './types';

/**
 * Downgrades a coverage snapshot to "unknown" for both the printer-level
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
  if (isOnline || !coverage) return coverage;
  return {
    ...coverage,
    status: 'unknown',
    toolheads: coverage.toolheads.map((th) => ({ ...th, status: 'unknown' })),
  };
}
