/**
 * Presentational badge + chip for filament coverage state (issue #717).
 *
 * The badge uses an icon **and** a text label so meaning doesn't depend
 * on color alone. It never claims "runout" when the underlying status is
 * `unknown`; the "unknown" case renders as a neutral "Filament unknown"
 * pill with an optional machine-readable reason as an accessible title.
 */
import React from "react";
import { AlertTriangleIcon, CheckIcon, HelpCircleIcon, ClockIcon } from "lucide-react";
import type {
  FilamentCoverageStatus,
  PrinterFilamentCoverage,
  ToolheadCoverage,
} from "../types";

export interface FilamentCoverageBadgeProps {
  status: FilamentCoverageStatus;
  /** Optional machine-readable reason surfaced through the accessible title. */
  reason?: string | null;
  /** Optional visible label override. */
  label?: string;
  /** Compact rendering: no visible text, icon-only (still has accessible name). */
  compact?: boolean;
  className?: string;
  /** Extra accessible-name context (e.g. printer name). */
  ariaContext?: string;
}

const STATUS_LABELS: Record<FilamentCoverageStatus, string> = {
  covers: "Filament OK",
  runout: "Runout risk",
  unknown: "Filament unknown",
};

const STATUS_CLASSES: Record<FilamentCoverageStatus, string> = {
  // Neutral success (green-ish) — icon carries the meaning too.
  covers:
    "bg-emerald-500/10 text-emerald-300 border-emerald-500/30",
  // Warning — never used unless the backend claims runout with real data.
  runout:
    "bg-amber-500/15 text-amber-200 border-amber-500/40",
  // Unknown is deliberately muted so it doesn't imply a warning.
  unknown:
    "bg-white/5 text-pf-text-tertiary border-white/10",
};

function StatusIcon({ status }: { status: FilamentCoverageStatus }): React.ReactElement {
  const common = "h-3.5 w-3.5 shrink-0";
  if (status === "covers") return <CheckIcon className={common} aria-hidden="true" />;
  if (status === "runout")
    return <AlertTriangleIcon className={common} aria-hidden="true" />;
  return <HelpCircleIcon className={common} aria-hidden="true" />;
}

/**
 * Icon + text badge summarizing coverage state for one printer or one
 * toolhead. Accessible name is always the visible label plus any
 * provided context.
 */
export function FilamentCoverageBadge({
  status,
  reason,
  label,
  compact = false,
  className,
  ariaContext,
}: FilamentCoverageBadgeProps): React.ReactElement {
  const visibleLabel = label ?? STATUS_LABELS[status];
  const accessibleName = ariaContext
    ? `${visibleLabel} — ${ariaContext}`
    : visibleLabel;
  const classes = [
    // A `role="status"` pill, which the design language puts at `xs`; the full
    // round is reserved for tag chips, and this is not one. Built as an array
    // literal rather than a `className` attribute, so the radius rule never saw it.
    "inline-flex items-center gap-1 rounded-xs border px-1.5 py-0.5 text-[11px] font-medium leading-none",
    STATUS_CLASSES[status],
    className ?? "",
  ]
    .join(" ")
    .trim();
  return (
    <span
      role="status"
      aria-label={accessibleName}
      title={reason ?? visibleLabel}
      className={classes}
      data-status={status}
    >
      <StatusIcon status={status} />
      {compact ? <span className="sr-only">{visibleLabel}</span> : <span>{visibleLabel}</span>}
    </span>
  );
}

export interface RunoutRiskChipProps {
  /** Predicted runout instant (UTC ISO) or null when unknown. */
  predictedRunoutAt: string | null;
  /** Predicted runout layer, when known. */
  predictedRunoutLayer?: number | null;
  className?: string;
}

function formatRelative(iso: string, now: Date = new Date()): string {
  const then = new Date(iso);
  if (Number.isNaN(then.getTime())) return "soon";
  const diffMs = then.getTime() - now.getTime();
  if (diffMs <= 0) return "imminent";
  const minutes = Math.round(diffMs / 60_000);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  const remaining = minutes % 60;
  if (hours < 24) return remaining > 0 ? `${hours}h ${remaining}m` : `${hours}h`;
  const days = Math.floor(hours / 24);
  const remH = hours % 24;
  return remH > 0 ? `${days}d ${remH}h` : `${days}d`;
}

/**
 * Chip showing the predicted-runout window for the active job. Renders
 * nothing when no runout is predicted — never invents a claim.
 */
export function RunoutRiskChip({
  predictedRunoutAt,
  predictedRunoutLayer,
  className,
}: RunoutRiskChipProps): React.ReactElement | null {
  if (!predictedRunoutAt && predictedRunoutLayer == null) return null;
  const relative = predictedRunoutAt ? formatRelative(predictedRunoutAt) : null;
  const layer = predictedRunoutLayer;
  const label = relative
    ? `Runs out in ${relative}`
    : layer != null
      ? `Runs out at layer ${layer}`
      : "Runout predicted";
  const title = layer != null
    ? `${label}${relative ? ` (layer ${layer})` : ""}`
    : label;
  return (
    <span
      role="status"
      aria-label={title}
      title={title}
      className={[
        "inline-flex items-center gap-1 rounded-xs border border-amber-500/40 bg-amber-500/15 px-1.5 py-0.5 text-[11px] font-medium text-amber-200",
        className ?? "",
      ]
        .join(" ")
        .trim()}
    >
      <ClockIcon className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
      <span>{label}</span>
    </span>
  );
}

export interface ToolheadCoverageRowProps {
  toolhead: ToolheadCoverage;
  className?: string;
}

/**
 * Detailed per-toolhead row for the printer details surface. Shows the
 * per-slot coverage badge and, when the active job predicts a runout on
 * this slot, an inline `RunoutRiskChip`.
 */
export function ToolheadCoverageRow({
  toolhead,
  className,
}: ToolheadCoverageRowProps): React.ReactElement {
  const remainingLabel =
    toolhead.remainingGrams != null
      ? `${Math.round(toolhead.remainingGrams)}g remaining`
      : "remaining unknown";
  const demandLabel =
    toolhead.totalDemandGrams != null
      ? `${Math.round(toolhead.totalDemandGrams)}g demand`
      : "demand unknown";
  return (
    <div
      className={[
        "flex flex-wrap items-center gap-2 text-xs text-pf-text-secondary",
        className ?? "",
      ]
        .join(" ")
        .trim()}
      data-testid={`toolhead-coverage-${toolhead.toolheadIndex}`}
    >
      <span className="font-medium text-pf-text-primary">
        {toolhead.toolheadName || `T${toolhead.toolheadIndex}`}
      </span>
      <FilamentCoverageBadge
        status={toolhead.status}
        reason={toolhead.statusReason}
        ariaContext={toolhead.toolheadName || `Toolhead ${toolhead.toolheadIndex}`}
      />
      <RunoutRiskChip
        predictedRunoutAt={toolhead.predictedRunoutAt}
        predictedRunoutLayer={toolhead.predictedRunoutLayer}
      />
      <span className="text-pf-text-tertiary">{remainingLabel}</span>
      <span className="text-pf-text-tertiary">·</span>
      <span className="text-pf-text-tertiary">{demandLabel}</span>
    </div>
  );
}

export interface PrinterCoverageSummaryProps {
  coverage: PrinterFilamentCoverage | null | undefined;
  /** Compact rendering (used by CompactPrinterCard filament row). */
  compact?: boolean;
  className?: string;
  /**
   * Whether the printer is currently reachable. Defaults to `true` so
   * existing callers that don't yet track connectivity keep their prior
   * behavior. When explicitly `false`, the coverage snapshot is treated as
   * stale/unverifiable and rendered as "unknown" regardless of what the
   * backend last reported — an offline printer must never show a "Filament
   * OK" success indicator (issue #1684).
   */
  isOnline?: boolean;
}

/**
 * One-line printer-level summary combining the aggregate badge with the
 * earliest-runout chip when the printer is currently at risk.
 */
export function PrinterCoverageSummary({
  coverage,
  compact = false,
  className,
  isOnline = true,
}: PrinterCoverageSummaryProps): React.ReactElement | null {
  if (!coverage) return null;
  // An offline printer's last-known coverage snapshot can't be trusted to
  // reflect reality (e.g. a spool could have been removed while
  // unreachable), so it must never be presented as a verified "covers" or
  // "runout" state. Fall back to "unknown" and suppress the runout chip.
  const status = isOnline ? coverage.status : "unknown";
  return (
    <span
      className={[
        "inline-flex items-center gap-1.5",
        className ?? "",
      ]
        .join(" ")
        .trim()}
      data-testid="printer-coverage-summary"
    >
      <FilamentCoverageBadge
        status={status}
        ariaContext={coverage.printerName || undefined}
        compact={compact}
      />
      {isOnline && coverage.status === "runout" && (
        <RunoutRiskChip
          predictedRunoutAt={coverage.earliestPredictedRunoutAt}
          predictedRunoutLayer={null}
        />
      )}
    </span>
  );
}
