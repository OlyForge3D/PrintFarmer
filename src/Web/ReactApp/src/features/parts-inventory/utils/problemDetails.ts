/**
 * Parsers for the canonical `application/problem+json` responses emitted
 * by the parts-inventory API. Axios flattens the `ApiError` shape defined
 * in `services/api.ts` (`{ message, statusCode, details }`), but the raw
 * ProblemDetails body is still attached on the `AxiosError.response.data`
 * when the caller keeps it. The helpers below tolerate both shapes so
 * component code can just ask "is this a wrongBin conflict?" without
 * caring which layer surfaced the error.
 */

import type {
  PartMappingRequiredDetails,
  WrongBinMismatch,
} from '@/types/partsInventory';

export const WRONG_BIN_CODE = 'wrongBin';
export const PART_MAPPING_REQUIRED_CODE = 'partMappingRequired';
export const FEATURE_DISABLED_CODE = 'featureDisabled';

interface ProblemDetailsLike {
  status?: number;
  title?: string;
  detail?: string;
  type?: string;
  code?: string;
  [extension: string]: unknown;
}

interface AxiosErrorLike {
  response?: { status?: number; data?: unknown };
  statusCode?: number;
  message?: string;
  details?: unknown;
}

function toProblemDetails(error: unknown): ProblemDetailsLike | null {
  if (!error || typeof error !== 'object') {
    return null;
  }
  const candidate = error as AxiosErrorLike & { data?: unknown };
  const raw = candidate.response?.data ?? candidate.details ?? candidate.data;
  if (raw && typeof raw === 'object' && !Array.isArray(raw)) {
    return raw as ProblemDetailsLike;
  }
  return null;
}

export function getProblemCode(error: unknown): string | null {
  const problem = toProblemDetails(error);
  if (!problem) return null;
  const value = problem.code;
  return typeof value === 'string' ? value : null;
}

export function isWrongBinError(error: unknown): boolean {
  return getProblemCode(error) === WRONG_BIN_CODE;
}

export function isPartMappingRequiredError(error: unknown): boolean {
  return getProblemCode(error) === PART_MAPPING_REQUIRED_CODE;
}

export function isFeatureDisabledError(error: unknown): boolean {
  return getProblemCode(error) === FEATURE_DISABLED_CODE;
}

export function getWrongBinMismatches(error: unknown): WrongBinMismatch[] {
  const problem = toProblemDetails(error);
  const value = problem?.mismatches;
  if (!Array.isArray(value)) return [];
  return value.filter((item): item is WrongBinMismatch => {
    if (!item || typeof item !== 'object') return false;
    const rec = item as Record<string, unknown>;
    return typeof rec.partSku === 'string' && typeof rec.scannedBinCode === 'string';
  });
}

export function getPartMappingRequiredDetails(
  error: unknown
): PartMappingRequiredDetails | null {
  const problem = toProblemDetails(error);
  if (!problem || problem.code !== PART_MAPPING_REQUIRED_CODE) return null;
  const jobId = problem.jobId;
  const guidance = problem.guidance;
  if (typeof jobId !== 'string' || typeof guidance !== 'string') return null;
  return {
    jobId,
    projectFileId: typeof problem.projectFileId === 'string' ? problem.projectFileId : null,
    gcodeFileId: typeof problem.gcodeFileId === 'string' ? problem.gcodeFileId : null,
    guidance,
  };
}

export function getErrorStatus(error: unknown): number | null {
  if (!error || typeof error !== 'object') return null;
  const candidate = error as AxiosErrorLike;
  const status = candidate.statusCode ?? candidate.response?.status;
  return typeof status === 'number' ? status : null;
}

export function getErrorMessage(error: unknown, fallback = 'Request failed'): string {
  if (!error) return fallback;
  const problem = toProblemDetails(error);
  if (problem?.detail && typeof problem.detail === 'string') {
    return problem.detail;
  }
  if (typeof error === 'string') return error;
  if (typeof error === 'object' && error !== null) {
    const candidate = error as { message?: unknown };
    if (typeof candidate.message === 'string' && candidate.message.length > 0) {
      return candidate.message;
    }
  }
  return fallback;
}
