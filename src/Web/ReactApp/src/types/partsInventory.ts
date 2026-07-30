/**
 * Printed-part inventory contracts (F9 / issue #714).
 *
 * These types mirror the backend camelCase JSON shape produced by
 * `PartsInventoryController`, `BinsController`, and the shared
 * ProblemDetails helpers. Adjustment reasons are serialized as the
 * string tokens defined by `PartAdjustmentReasonConverter` — never as
 * integer enum values — so the wire format is literal:
 *   "harvest" | "qc-reject" | "manual"
 *
 * These printed-part types are intentionally distinct from the
 * `maintenance/components` inventory used to service printers.
 */

export type PartAdjustmentReason = 'harvest' | 'qc-reject' | 'manual';

export const PART_ADJUSTMENT_REASONS: readonly PartAdjustmentReason[] = [
  'harvest',
  'qc-reject',
  'manual',
] as const;

export type PartHarvestOutputOrigin = 'mapping' | 'override' | 'fallback';

/** Response DTO for a printed-part SKU. Mirrors `PartInventoryResponse`. */
export interface PartInventoryDto {
  id: string;
  sku: string;
  name: string;
  description: string | null;
  modelFileRef: string | null;
  defaultBinId: string | null;
  defaultBinCode: string | null;
  defaultBinName: string | null;
  onHand: number;
  reorderPoint: number;
  needsReorder: boolean;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

/** Request body for creating a printed-part SKU. */
export interface CreatePartInventoryRequest {
  sku: string;
  name: string;
  description?: string | null;
  modelFileRef?: string | null;
  defaultBinCode?: string | null;
  initialOnHand?: number;
  reorderPoint?: number;
}

/** Request body for updating a printed-part SKU's mutable metadata. */
export interface UpdatePartInventoryRequest {
  name: string;
  description?: string | null;
  modelFileRef?: string | null;
  defaultBinCode?: string | null;
  reorderPoint: number;
  isActive: boolean;
}

/** Request body for a signed adjustment applied to a SKU's stock. */
export interface AdjustPartInventoryRequest {
  delta: number;
  reason: PartAdjustmentReason;
  jobId?: string | null;
  binCode?: string | null;
  notes?: string | null;
  /** Idempotency key — server dedupes retries of the same op. */
  operationKey?: string | null;
}

/** Response DTO for a single ledger entry. */
export interface PartAdjustmentDto {
  id: string;
  partInventoryId: string;
  sku: string;
  binId: string | null;
  binCode: string | null;
  delta: number;
  resultingBalance: number;
  reason: PartAdjustmentReason;
  printJobId: string | null;
  operationKey: string | null;
  notes: string | null;
  userId: string | null;
  createdAt: string;
}

/** Response DTO for a job-output → SKU mapping. */
export interface PartOutputMappingDto {
  id: string;
  partInventoryId: string;
  sku: string;
  gcodeFileId: string | null;
  printProjectFileId: string | null;
  quantity: number;
  createdAt: string;
  updatedAt: string;
}

/** Request body for creating a job-output → SKU mapping. */
export interface CreatePartOutputMappingRequest {
  sku: string;
  gcodeFileId?: string | null;
  printProjectFileId?: string | null;
  quantity: number;
}

/** Reorder-evaluation entry consumed by the F8 shift compiler and the web UI. */
export interface ReorderCandidateDto {
  partInventoryId: string;
  sku: string;
  name: string;
  onHand: number;
  reorderPoint: number;
  deficit: number;
}

/** Response DTO for a printed-part storage bin. */
export interface BinDto {
  id: string;
  code: string;
  name: string;
  location: string | null;
  notes: string | null;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

/** Request body for creating a bin. `code` doubles as the barcode. */
export interface CreateBinRequest {
  code: string;
  name: string;
  location?: string | null;
  notes?: string | null;
}

/** Request body for updating a bin. */
export interface UpdateBinRequest {
  name: string;
  location?: string | null;
  notes?: string | null;
  isActive: boolean;
}

/**
 * Request body for the bin registration endpoint that reuses the shared
 * barcode infrastructure. If a bin with the code already exists it is
 * returned (200); otherwise a new bin is created (201).
 */
export interface RegisterBinBarcodeRequest {
  code: string;
  name?: string | null;
  location?: string | null;
}

/** Extension shape for `code: "wrongBin"` conflicts on harvest. */
export interface WrongBinMismatch {
  partSku: string;
  expectedBinCode: string | null;
  scannedBinCode: string;
}

/** Extension shape for `code: "partMappingRequired"` conflicts. */
export interface PartMappingRequiredDetails {
  jobId: string;
  projectFileId: string | null;
  gcodeFileId: string | null;
  guidance: string;
}

/** Human-readable label for adjustment reasons in UI copy. */
export const PART_ADJUSTMENT_REASON_LABELS: Record<PartAdjustmentReason, string> = {
  harvest: 'Harvest',
  'qc-reject': 'QC reject',
  manual: 'Manual',
};
