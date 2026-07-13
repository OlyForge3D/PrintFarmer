/**
 * Printed-parts inventory & harvest DTOs.
 *
 * Mirrors backend `Farm.Infrastructure.Dtos.PartsInventory` (PR #741). These
 * types are consumed by the web Harvest action introduced for #722. Casing
 * follows the API JSON contract (camelCase) — do NOT re-encode enum values
 * as integers.
 */

/** Enum values match backend `PartAdjustmentReason` string names. */
export type PartAdjustmentReason =
  | 'Harvest'
  | 'QualityReject'
  | 'Manual';

/** Enum values match backend `PartHarvestOutputOrigin` string names. */
export type PartHarvestOutputOrigin =
  | 'Mapped'
  | 'Manual'
  | 'Fallback';

/** Response DTO for a printed-part SKU. */
export interface PartInventoryResponse {
  id: string;
  sku: string;
  name: string;
  description?: string | null;
  modelFileRef?: string | null;
  defaultBinId?: string | null;
  defaultBinCode?: string | null;
  defaultBinName?: string | null;
  onHand: number;
  reorderPoint: number;
  needsReorder: boolean;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

/** Response DTO for a printed-part storage bin. */
export interface BinResponse {
  id: string;
  code: string;
  name: string;
  location?: string | null;
  notes?: string | null;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

/** Single immutable ledger entry produced by a harvest or manual adjustment. */
export interface PartAdjustmentResponse {
  id: string;
  partInventoryId: string;
  sku: string;
  binId?: string | null;
  binCode?: string | null;
  delta: number;
  resultingBalance: number;
  reason: PartAdjustmentReason;
  printJobId?: string | null;
  operationKey?: string | null;
  notes?: string | null;
  userId?: string | null;
  createdAt: string;
}

/** Job-output → SKU mapping row (drives Harvest prefill). */
export interface PartOutputMappingResponse {
  id: string;
  partInventoryId: string;
  sku: string;
  gcodeFileId?: string | null;
  printProjectFileId?: string | null;
  quantity: number;
  createdAt: string;
  updatedAt: string;
}

/** Persisted final output returned by a successful (or replayed) harvest. */
export interface HarvestOutputResponse {
  sequence: number;
  partInventoryId: string;
  partSku: string;
  quantity: number;
  expectedBinId?: string | null;
  expectedBinCode?: string | null;
  actualBinId: string;
  actualBinCode: string;
  origin: PartHarvestOutputOrigin;
  sourceFileId?: string | null;
  sourceMappingId?: string | null;
  overrideApplied: boolean;
  overrideReason?: string | null;
  createdAt: string;
}

/** Response DTO for the harvest action on a completed print job. */
export interface HarvestJobResponse {
  printJobId: string;
  harvestedAt: string;
  binId?: string | null;
  binCode?: string | null;
  /**
   * True when this response is an idempotent replay of a previous harvest.
   * The web UI uses this to display a "already harvested" state and avoid
   * duplicate success toasts.
   */
  alreadyHarvested: boolean;
  adjustments: PartAdjustmentResponse[];
  outputs: HarvestOutputResponse[];
}

/** Per-SKU override item on the harvest request (manual/fallback outputs). */
export interface HarvestOutputRequestItem {
  sku: string;
  quantity: number;
}

/** Per-SKU destination bin assignment for a multi-output harvest. */
export interface HarvestOutputBinRequest {
  partSku: string;
  binCode: string;
}

/** Request body for `POST /api/job-queue/{id}/harvest`. */
export interface HarvestJobRequest {
  /** Shared destination bin (used when `outputBins` is empty). */
  binCode?: string | null;
  /**
   * Uniform quantity override applied to every mapped SKU. Prefer using
   * `outputs` with explicit per-SKU quantities for multi-SKU plates.
   */
  quantityOverride?: number | null;
  /**
   * Explicit per-SKU quantities. Required when the job has no mapping
   * (server responded with `partMappingRequired`), or when the caller
   * needs to correct mapped quantities.
   */
  outputs?: HarvestOutputRequestItem[] | null;
  /**
   * Client-generated idempotency key. Replaying with the same key returns
   * the original result without applying deltas twice.
   */
  operationKey?: string | null;
  /** Per-SKU destination bin overrides. */
  outputBins?: HarvestOutputBinRequest[] | null;
  /**
   * When true, the server proceeds even if the destination bin does not
   * match the SKU's default bin. Must be accompanied by `overrideReason`.
   */
  allowWrongBin?: boolean;
  /** Audit-logged reason for a wrong-bin override. */
  overrideReason?: string | null;
}

/** Single wrong-bin mismatch reported in the canonical 409 ProblemDetails. */
export interface WrongBinMismatchResponse {
  partSku: string;
  expectedBinCode?: string | null;
  scannedBinCode: string;
}

/** Payload for the canonical `wrongBin` ProblemDetails extensions. */
export interface WrongBinDetails {
  code: 'wrongBin';
  mismatches: WrongBinMismatchResponse[];
}

/** Payload for the canonical `partMappingRequired` ProblemDetails extensions. */
export interface PartMappingRequiredDetails {
  code: 'partMappingRequired';
  jobId: string;
  projectFileId?: string | null;
  gcodeFileId?: string | null;
  guidance: string;
}
