import type { ApiError } from '@/types/api';

/**
 * Structured optimistic-concurrency conflict body returned by revision-aware endpoints
 * (e.g. `PUT /api/tags/{id}`) on HTTP 409/412. See TagConcurrencyException (#844).
 */
export interface RevisionConflictInfo {
  error: string;
  expectedRevision: number;
  actualRevision: number;
}

/** Narrows an unknown thrown value to the shared `ApiError` shape. */
export function isApiError(error: unknown): error is ApiError {
  return (
    typeof error === 'object' &&
    error !== null &&
    'statusCode' in error &&
    typeof (error as ApiError).statusCode === 'number'
  );
}

/**
 * Detects a structured revision/optimistic-concurrency conflict (HTTP 409 or 412) carrying
 * `expectedRevision`/`actualRevision` in the response body, and returns the parsed conflict
 * info. Returns `null` for any other error shape so callers can fall back to generic
 * error handling without ever silently discarding the user's attempted change.
 */
export function getRevisionConflict(error: unknown): RevisionConflictInfo | null {
  if (!isApiError(error)) return null;
  if (error.statusCode !== 409 && error.statusCode !== 412) return null;

  const body = error.data as Partial<RevisionConflictInfo> | undefined;
  if (
    !body ||
    typeof body.expectedRevision !== 'number' ||
    typeof body.actualRevision !== 'number'
  ) {
    return null;
  }

  return {
    error: typeof body.error === 'string' ? body.error : error.message,
    expectedRevision: body.expectedRevision,
    actualRevision: body.actualRevision,
  };
}
