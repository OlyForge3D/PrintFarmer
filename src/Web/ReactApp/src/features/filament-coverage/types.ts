/**
 * Filament coverage & runout risk contract (issue #709 / #717).
 *
 * Wire tokens are canonical lowercase: `unknown | covers | runout`.
 * The decoder tolerates transitional / legacy uppercase forms
 * (`Unknown | Covers | Runout | Insufficient`) at the boundary only —
 * callers of `decodeFilamentCoverageStatus` always receive the canonical
 * lowercase union.
 *
 * SignalR event: `filamentcoveragechanged` with payload
 * `{ printerId, reason, occurredAt }`. Reasons are canonical strings from
 * `FilamentCoverageChangeReason`; the payload is an invalidation cue, not
 * a data-truth source — callers must refetch canonical queries.
 */

export type FilamentCoverageStatus = "unknown" | "covers" | "runout";

export const FILAMENT_COVERAGE_STATUSES: readonly FilamentCoverageStatus[] = [
  "unknown",
  "covers",
  "runout",
] as const;

/**
 * Machine-readable change reasons emitted with `filamentcoveragechanged`.
 * Kept as a union of strings so unknown/future reasons still round-trip.
 */
export type FilamentCoverageChangeReason =
  | "jobProgress"
  | "jobAssignment"
  | "queueChanged"
  | "spoolBinding"
  | "spoolWeight"
  | "thresholdChanged"
  | (string & { readonly __unknownReason?: unique symbol });

export interface FilamentCoverageChangedEvent {
  /** Printer whose coverage changed, or `null` for a fleet-wide invalidation. */
  printerId: string | null;
  reason: FilamentCoverageChangeReason;
  occurredAt: string;
}

export interface ToolheadCoverage {
  toolheadIndex: number;
  toolheadName: string;
  spoolId: number | null;
  material: string | null;
  filamentColor: string | null;
  remainingGrams: number | null;
  currentJobRequiredGrams: number | null;
  currentJobRemainingGrams: number | null;
  queuedRequiredGrams: number | null;
  totalDemandGrams: number | null;
  status: FilamentCoverageStatus;
  statusReason: string | null;
  predictedRunoutAt: string | null;
  predictedRunoutLayer: number | null;
}

export interface PrinterFilamentCoverage {
  printerId: string;
  printerName: string;
  status: FilamentCoverageStatus;
  toolheads: ToolheadCoverage[];
  activeJobId: string | null;
  activeJobName: string | null;
  activeJobProgress: number | null;
  earliestPredictedRunoutAt: string | null;
  assignedQueuedJobCount: number;
  evaluatedAtUtc: string;
}

export interface FleetFilamentCoverage {
  printers: PrinterFilamentCoverage[];
  evaluatedAtUtc: string;
}

/**
 * Decode a possibly-legacy status string into the canonical lowercase union.
 * `Insufficient` is mapped to `runout` for historical clients; any other
 * unknown value is treated as `unknown` (never `runout`), consistent with
 * the never-claim-runout-when-unknown invariant.
 */
export function decodeFilamentCoverageStatus(
  raw: string | null | undefined,
): FilamentCoverageStatus {
  if (typeof raw !== "string") return "unknown";
  const normalized = raw.toLowerCase();
  if (normalized === "covers") return "covers";
  if (normalized === "runout" || normalized === "insufficient") return "runout";
  return "unknown";
}

/** Decode a raw toolhead payload from the API (camelCase JSON). */
export function decodeToolheadCoverage(raw: unknown): ToolheadCoverage {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    toolheadIndex: numberOr(r.toolheadIndex, 0),
    toolheadName: stringOr(r.toolheadName, ""),
    spoolId: nullableNumber(r.spoolId),
    material: nullableString(r.material),
    filamentColor: nullableString(r.filamentColor),
    remainingGrams: nullableNumber(r.remainingGrams),
    currentJobRequiredGrams: nullableNumber(r.currentJobRequiredGrams),
    currentJobRemainingGrams: nullableNumber(r.currentJobRemainingGrams),
    queuedRequiredGrams: nullableNumber(r.queuedRequiredGrams),
    totalDemandGrams: nullableNumber(r.totalDemandGrams),
    status: decodeFilamentCoverageStatus(r.status as string | null | undefined),
    statusReason: nullableString(r.statusReason),
    predictedRunoutAt: nullableString(r.predictedRunoutAt),
    predictedRunoutLayer: nullableNumber(r.predictedRunoutLayer),
  };
}

export function decodePrinterFilamentCoverage(raw: unknown): PrinterFilamentCoverage {
  const r = (raw ?? {}) as Record<string, unknown>;
  const toolheadsRaw = Array.isArray(r.toolheads) ? (r.toolheads as unknown[]) : [];
  return {
    printerId: stringOr(r.printerId, ""),
    printerName: stringOr(r.printerName, ""),
    status: decodeFilamentCoverageStatus(r.status as string | null | undefined),
    toolheads: toolheadsRaw.map(decodeToolheadCoverage),
    activeJobId: nullableString(r.activeJobId),
    activeJobName: nullableString(r.activeJobName),
    activeJobProgress: nullableNumber(r.activeJobProgress),
    earliestPredictedRunoutAt: nullableString(r.earliestPredictedRunoutAt),
    assignedQueuedJobCount: numberOr(r.assignedQueuedJobCount, 0),
    evaluatedAtUtc: stringOr(r.evaluatedAtUtc, new Date(0).toISOString()),
  };
}

export function decodeFleetFilamentCoverage(raw: unknown): FleetFilamentCoverage {
  const r = (raw ?? {}) as Record<string, unknown>;
  const printersRaw = Array.isArray(r.printers) ? (r.printers as unknown[]) : [];
  return {
    printers: printersRaw.map(decodePrinterFilamentCoverage),
    evaluatedAtUtc: stringOr(r.evaluatedAtUtc, new Date(0).toISOString()),
  };
}

export function decodeFilamentCoverageChangedEvent(
  raw: unknown,
): FilamentCoverageChangedEvent {
  const r = (raw ?? {}) as Record<string, unknown>;
  const printerId = typeof r.printerId === "string" ? r.printerId : null;
  const reason = typeof r.reason === "string" ? r.reason : "unknown";
  const occurredAt = typeof r.occurredAt === "string" ? r.occurredAt : new Date().toISOString();
  return { printerId, reason, occurredAt };
}

// --- small local helpers -------------------------------------------------

function nullableString(value: unknown): string | null {
  return typeof value === "string" ? value : null;
}

function nullableNumber(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function stringOr(value: unknown, fallback: string): string {
  return typeof value === "string" ? value : fallback;
}

function numberOr(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}
