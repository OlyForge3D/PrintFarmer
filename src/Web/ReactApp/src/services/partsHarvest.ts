/**
 * Typed service wrapper for the printed-parts harvest API (#722/#741).
 *
 * Wraps `POST /api/job-queue/{id}/harvest` plus supporting `parts-inventory`
 * reads, translating axios errors into a discriminated `HarvestError` union
 * so the UI can render canonical wrongBin / partMappingRequired /
 * featureDisabled flows without success-shaped fallbacks.
 */

import { apiClient } from '@/services/api';
import type {
  HarvestJobRequest,
  HarvestJobResponse,
  PartInventoryResponse,
  PartOutputMappingResponse,
  PartMappingRequiredDetails,
  WrongBinMismatchResponse,
} from '@/types/parts-inventory';

/**
 * Discriminated union of the errors `harvestJob` may throw. The `kind`
 * field is exhaustive — callers should switch on it before rendering.
 */
export type HarvestError =
  | { kind: 'featureDisabled'; message: string; status: number }
  | { kind: 'jobNotFound'; message: string; status: number }
  | { kind: 'jobNotCompleted'; message: string; status: number }
  | { kind: 'binNotFound'; message: string; status: number }
  | { kind: 'partNotFound'; message: string; status: number }
  | { kind: 'invalidRequest'; message: string; status: number }
  | { kind: 'conflict'; message: string; status: number }
  | {
      kind: 'wrongBin';
      message: string;
      status: number;
      mismatches: WrongBinMismatchResponse[];
    }
  | {
      kind: 'partMappingRequired';
      message: string;
      status: number;
      details: PartMappingRequiredDetails;
    }
  | { kind: 'network'; message: string }
  | { kind: 'unknown'; message: string; status?: number };

export class HarvestServiceError extends Error {
  public readonly info: HarvestError;

  public constructor(info: HarvestError) {
    super(info.message);
    this.name = 'HarvestServiceError';
    this.info = info;
  }
}

/**
 * True when the value looks like a ProblemDetails payload from the API
 * (has a numeric status and either a canonical `code` extension or a
 * `title`). Kept intentionally loose because ASP.NET Core surfaces both
 * `application/problem+json` and plain `{ message }` bodies depending on
 * the code path.
 */
function isProblemDetailsShape(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function readCode(body: Record<string, unknown>): string | null {
  const code = body['code'];
  return typeof code === 'string' && code.length > 0 ? code : null;
}

function readMessage(body: Record<string, unknown>, fallback: string): string {
  const detail = body['detail'];
  if (typeof detail === 'string' && detail.length > 0) return detail;
  const title = body['title'];
  if (typeof title === 'string' && title.length > 0) return title;
  const message = body['message'];
  if (typeof message === 'string' && message.length > 0) return message;
  return fallback;
}

function readMismatches(body: Record<string, unknown>): WrongBinMismatchResponse[] {
  const raw = body['mismatches'];
  if (!Array.isArray(raw)) return [];
  const results: WrongBinMismatchResponse[] = [];
  for (const entry of raw) {
    if (!isProblemDetailsShape(entry)) continue;
    const partSku = entry['partSku'];
    const scannedBinCode = entry['scannedBinCode'];
    if (typeof partSku !== 'string' || typeof scannedBinCode !== 'string') continue;
    const expectedRaw = entry['expectedBinCode'];
    const expectedBinCode = typeof expectedRaw === 'string' ? expectedRaw : null;
    results.push({ partSku, expectedBinCode, scannedBinCode });
  }
  return results;
}

function readMappingRequired(
  body: Record<string, unknown>,
): PartMappingRequiredDetails | null {
  const jobId = body['jobId'];
  const guidance = body['guidance'];
  if (typeof jobId !== 'string' || typeof guidance !== 'string') return null;
  const projectFileIdRaw = body['projectFileId'];
  const gcodeFileIdRaw = body['gcodeFileId'];
  return {
    code: 'partMappingRequired',
    jobId,
    projectFileId: typeof projectFileIdRaw === 'string' ? projectFileIdRaw : null,
    gcodeFileId: typeof gcodeFileIdRaw === 'string' ? gcodeFileIdRaw : null,
    guidance,
  };
}

interface AxiosLikeError {
  isAxiosError: true;
  message?: string;
  response?: { status: number; data?: unknown };
}

function isAxiosLikeError(error: unknown): error is AxiosLikeError {
  return (
    error !== null &&
    typeof error === 'object' &&
    (error as { isAxiosError?: unknown }).isAxiosError === true
  );
}

/**
 * Convert an axios error into a typed `HarvestError`. Exported for tests.
 */
export function toHarvestError(error: unknown): HarvestError {
  if (!isAxiosLikeError(error)) {
    return {
      kind: 'unknown',
      message: error instanceof Error ? error.message : 'Harvest failed',
    };
  }

  const axiosError = error;

  if (!axiosError.response) {
    return {
      kind: 'network',
      message: axiosError.message || 'Network error while contacting the API.',
    };
  }

  const status = axiosError.response.status;
  const body = isProblemDetailsShape(axiosError.response.data)
    ? axiosError.response.data
    : {};
  const code = readCode(body);

  // Canonical ProblemDetails codes take priority over status-based inference.
  if (code === 'wrongBin') {
    return {
      kind: 'wrongBin',
      status,
      message: readMessage(body, 'One or more scanned destination bins do not match the expected bins.'),
      mismatches: readMismatches(body),
    };
  }

  if (code === 'partMappingRequired') {
    const details = readMappingRequired(body);
    if (details) {
      return {
        kind: 'partMappingRequired',
        status,
        message: readMessage(body, 'Printed-part mapping required.'),
        details,
      };
    }
  }

  if (code === 'featureDisabled' || code === 'operatorFeatureDisabled') {
    return {
      kind: 'featureDisabled',
      status,
      message: readMessage(body, 'Printed-parts inventory is not enabled on this server.'),
    };
  }

  if (status === 404) {
    // Feature-gated endpoints on this feature also return 404 with a
    // ProblemDetails code (see backend `OperatorFeatureProblemDetails`).
    const message = readMessage(body, 'Not found.');
    if (/feature/i.test(message) || /enabled/i.test(message)) {
      return { kind: 'featureDisabled', status, message };
    }
    if (/job/i.test(message)) return { kind: 'jobNotFound', status, message };
    if (/sku/i.test(message) || /part/i.test(message)) {
      return { kind: 'partNotFound', status, message };
    }
    return { kind: 'jobNotFound', status, message };
  }

  if (status === 409) {
    const message = readMessage(body, 'Harvest conflict.');
    if (/not completed/i.test(message) || /not\s+complete/i.test(message)) {
      return { kind: 'jobNotCompleted', status, message };
    }
    return { kind: 'conflict', status, message };
  }

  if (status === 400) {
    const message = readMessage(body, 'Invalid harvest request.');
    if (/bin/i.test(message)) return { kind: 'binNotFound', status, message };
    return { kind: 'invalidRequest', status, message };
  }

  return {
    kind: 'unknown',
    status,
    message: readMessage(body, `Harvest failed with status ${status}.`),
  };
}

type HarvestHttpClient = {
  get: <T = unknown>(url: string, config?: { params?: unknown }) => Promise<{ data: T }>;
  post: <T = unknown>(url: string, data?: unknown) => Promise<{ data: T }>;
};

/**
 * Access to the shared HTTP client. Tests can substitute a stub via
 * `configurePartsHarvestClient`.
 */
let httpOverride: HarvestHttpClient | null = null;

/** Test-only: replace the HTTP client used by this service. */
export function configurePartsHarvestClient(client: HarvestHttpClient | null): void {
  httpOverride = client;
}

function getClient(): HarvestHttpClient {
  if (httpOverride) return httpOverride;
  return apiClient as unknown as HarvestHttpClient;
}

/** Harvest a completed print job into printed-part stock. */
export async function harvestJob(
  jobId: string,
  request: HarvestJobRequest,
): Promise<HarvestJobResponse> {
  try {
    const response = await getClient().post(
      `/job-queue/${encodeURIComponent(jobId)}/harvest`,
      request,
    );
    return response.data as HarvestJobResponse;
  } catch (error) {
    throw new HarvestServiceError(toHarvestError(error));
  }
}

/** List printed-part SKUs (used to resolve default bins in the dialog). */
export async function listParts(
  options: { includeInactive?: boolean } = {},
): Promise<PartInventoryResponse[]> {
  try {
    const response = await getClient().get('/parts-inventory', {
      params: { includeInactive: options.includeInactive ?? false },
    });
    return response.data as PartInventoryResponse[];
  } catch (error) {
    throw new HarvestServiceError(toHarvestError(error));
  }
}

/**
 * Fetch job-output → SKU mappings, optionally filtered to a specific SKU.
 * The web Harvest dialog uses this to preview which SKUs a job produces
 * before mutating stock.
 */
export async function listMappings(
  filter: { sku?: string } = {},
): Promise<PartOutputMappingResponse[]> {
  try {
    const response = await getClient().get('/parts-inventory/mappings', {
      params: filter.sku ? { sku: filter.sku } : undefined,
    });
    return response.data as PartOutputMappingResponse[];
  } catch (error) {
    throw new HarvestServiceError(toHarvestError(error));
  }
}

/**
 * Generate a UUIDv4-shaped operation key for idempotent harvest replay.
 * Falls back to a Math.random string when crypto.randomUUID is unavailable.
 */
export function generateHarvestOperationKey(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}
