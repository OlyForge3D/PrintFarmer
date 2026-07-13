/**
 * Printed-parts inventory service (issue #714 / F9).
 *
 * Wraps the `/api/parts-inventory` and `/api/bins` REST surface. All calls
 * flow through the shared `apiClient` axios instance so auth, correlation
 * IDs, and 401 handling remain centralized. The service is intentionally
 * distinct from `maintenanceService`: printed parts and maintenance
 * components are separate inventory domains.
 *
 * Stock changes never bypass the adjustment ledger. Callers use
 * {@link partsInventoryService.adjustStock} to record signed deltas with
 * an idempotent `operationKey`; `updatePart` intentionally cannot rewrite
 * `onHand`.
 */

import { apiClient } from '@/services/api';
import type {
  AdjustPartInventoryRequest,
  BinDto,
  CreateBinRequest,
  CreatePartInventoryRequest,
  CreatePartOutputMappingRequest,
  PartAdjustmentDto,
  PartInventoryDto,
  PartOutputMappingDto,
  RegisterBinBarcodeRequest,
  ReorderCandidateDto,
  UpdateBinRequest,
  UpdatePartInventoryRequest,
} from '@/types/partsInventory';

const PARTS_BASE = '/parts-inventory';
const BINS_BASE = '/bins';
const DEFAULT_ADJUSTMENT_LIMIT = 100;

/**
 * Result of registering a barcode against a bin. `wasCreated` is true
 * when the server responded 201 (fresh bin), false when 200 (existing).
 */
export interface RegisterBinBarcodeResult {
  bin: BinDto;
  wasCreated: boolean;
}

function encodeSegment(value: string): string {
  return encodeURIComponent(value);
}

export const partsInventoryService = {
  // ── Printed-part SKUs ────────────────────────────────────────────────

  async listParts(options?: { includeInactive?: boolean }): Promise<PartInventoryDto[]> {
    const { data } = await apiClient.get<PartInventoryDto[]>(PARTS_BASE, {
      params: { includeInactive: options?.includeInactive ?? false },
    });
    return data;
  },

  async getPart(sku: string): Promise<PartInventoryDto> {
    const { data } = await apiClient.get<PartInventoryDto>(`${PARTS_BASE}/${encodeSegment(sku)}`);
    return data;
  },

  async resolvePartByBarcode(code: string): Promise<PartInventoryDto> {
    const { data } = await apiClient.get<PartInventoryDto>(
      `${PARTS_BASE}/by-barcode/${encodeSegment(code)}`
    );
    return data;
  },

  async createPart(request: CreatePartInventoryRequest): Promise<PartInventoryDto> {
    const { data } = await apiClient.post<PartInventoryDto>(PARTS_BASE, request);
    return data;
  },

  async updatePart(sku: string, request: UpdatePartInventoryRequest): Promise<PartInventoryDto> {
    const { data } = await apiClient.put<PartInventoryDto>(
      `${PARTS_BASE}/${encodeSegment(sku)}`,
      request
    );
    return data;
  },

  /** Soft-deactivates the SKU. The immutable ledger and mappings are retained. */
  async deletePart(sku: string): Promise<void> {
    await apiClient.delete<void>(`${PARTS_BASE}/${encodeSegment(sku)}`);
  },

  /**
   * Applies a signed adjustment to the SKU. `operationKey` provides
   * idempotency across client retries — a replay returns the same
   * ledger row rather than double-applying the delta.
   */
  async adjustStock(sku: string, request: AdjustPartInventoryRequest): Promise<PartAdjustmentDto> {
    const { data } = await apiClient.post<PartAdjustmentDto>(
      `${PARTS_BASE}/${encodeSegment(sku)}/adjust`,
      request
    );
    return data;
  },

  async listAdjustments(sku: string, limit: number = DEFAULT_ADJUSTMENT_LIMIT): Promise<PartAdjustmentDto[]> {
    const { data } = await apiClient.get<PartAdjustmentDto[]>(
      `${PARTS_BASE}/${encodeSegment(sku)}/adjustments`,
      { params: { limit } }
    );
    return data;
  },

  async listReorderCandidates(): Promise<ReorderCandidateDto[]> {
    const { data } = await apiClient.get<ReorderCandidateDto[]>(`${PARTS_BASE}/reorder`);
    return data;
  },

  // ── Output mappings ──────────────────────────────────────────────────

  async listMappings(sku?: string): Promise<PartOutputMappingDto[]> {
    const { data } = await apiClient.get<PartOutputMappingDto[]>(`${PARTS_BASE}/mappings`, {
      params: sku ? { sku } : undefined,
    });
    return data;
  },

  async createMapping(request: CreatePartOutputMappingRequest): Promise<PartOutputMappingDto> {
    const { data } = await apiClient.post<PartOutputMappingDto>(`${PARTS_BASE}/mappings`, request);
    return data;
  },

  async deleteMapping(id: string): Promise<void> {
    await apiClient.delete<void>(`${PARTS_BASE}/mappings/${encodeSegment(id)}`);
  },

  // ── Bins ─────────────────────────────────────────────────────────────

  async listBins(options?: { includeInactive?: boolean }): Promise<BinDto[]> {
    const { data } = await apiClient.get<BinDto[]>(BINS_BASE, {
      params: { includeInactive: options?.includeInactive ?? false },
    });
    return data;
  },

  async getBin(code: string): Promise<BinDto> {
    const { data } = await apiClient.get<BinDto>(`${BINS_BASE}/${encodeSegment(code)}`);
    return data;
  },

  async resolveBinByBarcode(code: string): Promise<BinDto> {
    const { data } = await apiClient.get<BinDto>(`${BINS_BASE}/by-barcode/${encodeSegment(code)}`);
    return data;
  },

  async createBin(request: CreateBinRequest): Promise<BinDto> {
    const { data } = await apiClient.post<BinDto>(BINS_BASE, request);
    return data;
  },

  async updateBin(code: string, request: UpdateBinRequest): Promise<BinDto> {
    const { data } = await apiClient.put<BinDto>(`${BINS_BASE}/${encodeSegment(code)}`, request);
    return data;
  },

  /**
   * Registers a bin from a scanned barcode. Returns the existing bin if
   * the code is already registered; otherwise creates and returns a new
   * bin. Callers should inspect the response `id/createdAt` rather than
   * assume the resulting HTTP status, since the axios wrapper flattens
   * 200 and 201 into the same shape.
   */
  async registerBinBarcode(request: RegisterBinBarcodeRequest): Promise<RegisterBinBarcodeResult> {
    const response = await apiClient.post<BinDto>(`${BINS_BASE}/register`, request);
    return {
      bin: response.data,
      wasCreated: response.status === 201,
    };
  },

  async deleteBin(code: string): Promise<void> {
    await apiClient.delete<void>(`${BINS_BASE}/${encodeSegment(code)}`);
  },
};

export type PartsInventoryService = typeof partsInventoryService;
