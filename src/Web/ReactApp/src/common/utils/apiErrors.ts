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

/**
 * Extracts a human-readable message from an ASP.NET Core
 * `ValidationProblemDetails`-style `errors` dictionary, e.g.
 * `{ errors: { "$.model3DId": ["The JSON value could not be converted to
 * System.Nullable`1[System.Guid]. Path: $.model3DId"] } }`. Automatic model
 * binding failures (a malformed request body) return this shape with no
 * top-level `message`/`detail` field, so without this the caller only ever
 * sees a generic "Request failed with status code 400" (issue #1973).
 * Returns undefined when the body has no such `errors` map.
 */
export function extractValidationErrorMessage(data: unknown): string | undefined {
  if (!data || typeof data !== 'object') return undefined;
  const errors = (data as { errors?: unknown }).errors;
  if (!errors || typeof errors !== 'object') return undefined;

  const messages = Object.values(errors as Record<string, unknown>)
    .flatMap((value) => (Array.isArray(value) ? value : [value]))
    .filter((value): value is string => typeof value === 'string' && value.length > 0);

  return messages.length > 0 ? messages.join(' ') : undefined;
}

/**
 * Extracts a human-readable message from a raw XHR error response body. XHR-based upload
 * flows (progress-tracked via `XMLHttpRequest` rather than the `apiClient` axios instance)
 * previously discarded the server's response body entirely and surfaced only
 * `xhr.statusText` (e.g. "Bad Request"), hiding actionable validation detail such as
 * "File is too small to be a valid STL (must be at least 84 bytes)" (issue #2175). Mirrors
 * the axios interceptor's message-extraction priority above: a top-level `message`/`detail`
 * string, then a `ValidationProblemDetails`-style `errors` map, then a bare JSON string body
 * (ASP.NET Core's `BadRequest(string)` serializes the string as-is), then raw response text,
 * finally `fallback`.
 */
export function parseXhrErrorMessage(responseText: string, fallback: string): string {
  if (!responseText) return fallback;

  let data: unknown;
  try {
    data = JSON.parse(responseText);
  } catch {
    return responseText.trim() || fallback;
  }

  if (typeof data === 'string') return data || fallback;

  if (data && typeof data === 'object') {
    const record = data as { message?: unknown; detail?: unknown; error?: unknown };
    if (typeof record.message === 'string' && record.message) return record.message;
    if (typeof record.detail === 'string' && record.detail) return record.detail;
    if (typeof record.error === 'string' && record.error) return record.error;

    const validationMessage = extractValidationErrorMessage(data);
    if (validationMessage) return validationMessage;
  }

  return fallback;
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
 * Extracts a human-readable message from an unknown thrown value. `apiClient` rejects with a
 * plain `ApiError` object (built by the Axios response interceptor), not an `Error` instance,
 * so `error instanceof Error` is *not* sufficient to recover the real server-provided message -
 * it would silently fall back to a generic string for every real API failure. Prefers the
 * `ApiError.message` set by the interceptor, then a native `Error.message`, then `fallback`.
 */
export function getErrorMessage(error: unknown, fallback: string): string {
  if (isApiError(error) && error.message) return error.message;
  if (error instanceof Error) return error.message;
  return fallback;
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
