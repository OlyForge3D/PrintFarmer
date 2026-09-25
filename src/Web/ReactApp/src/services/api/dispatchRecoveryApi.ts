import { client } from '@/services/api/httpClient';
import type {
  DispatchReconciliationResource,
  DispatchReconciliationSnapshot,
  DispatchRecoveryAudit,
  DispatchRecoveryClearResult,
  DispatchRecoveryRequest,
  DispatchRecoveryResult,
} from '@/types/api';

/**
 * Dispatch recovery API (issue #2993, backend #2859). Every mutation is
 * revision-fenced with `If-Match`; recovery is additionally replay-safe via
 * `Idempotency-Key`. Callers must never update caches optimistically — the
 * server response (and a refetch) is the only source of truth.
 */

const DISPATCH_API_BASE = '/dispatch';
const HANDLED_STATUSES = [200, 400, 403, 404, 409, 412, 428];

interface ErrorBody {
  error?: string;
  detail?: string | null;
  recoveryAuditId?: string | null;
  liveSender?: string | null;
}

function quotedEtag(value: string | null | undefined, label: string): string {
  const reviewed = (value ?? '').trim();
  if (!reviewed) {
    throw new Error(`${label} does not have a reviewed ETag`);
  }
  return reviewed.startsWith('"') || reviewed.startsWith('W/')
    ? reviewed
    : `"${reviewed}"`;
}

function readEtag(headers: unknown): string | null {
  if (!headers || typeof headers !== 'object') {
    return null;
  }
  const value = (headers as Record<string, unknown>).etag;
  return typeof value === 'string' && value.trim() ? value.trim() : null;
}

function failureKind(
  status: number
): 'stale' | 'conflict' | 'invalid' | 'forbidden' | 'not_found' {
  switch (status) {
    case 412:
      return 'stale';
    case 409:
      return 'conflict';
    case 403:
      return 'forbidden';
    case 404:
      return 'not_found';
    default:
      return 'invalid';
  }
}

export async function getDispatchReconciliation(
  printerId: string
): Promise<DispatchReconciliationSnapshot> {
  const response = await client.get<DispatchReconciliationResource>(
    `${DISPATCH_API_BASE}/${encodeURIComponent(printerId)}/reconciliation`
  );
  return { resource: response.data, etag: readEtag(response.headers) };
}

export async function recoverDispatchClaim(input: {
  printerId: string;
  etag: string;
  idempotencyKey: string;
  body: DispatchRecoveryRequest;
}): Promise<DispatchRecoveryResult> {
  const idempotencyKey = input.idempotencyKey.trim();
  if (!idempotencyKey) {
    throw new Error('Dispatch recovery requires an Idempotency-Key');
  }
  const response = await client.post<DispatchReconciliationResource & ErrorBody>(
    `${DISPATCH_API_BASE}/${encodeURIComponent(input.printerId)}/reconciliation/recover`,
    input.body,
    {
      headers: {
        'If-Match': quotedEtag(input.etag, 'The reviewed dispatch claim'),
        'Idempotency-Key': idempotencyKey,
      },
      validateStatus: (status) => HANDLED_STATUSES.includes(status),
    }
  );
  if (response.status === 200) {
    return {
      kind: 'recovered',
      httpStatus: 200,
      resource: response.data,
      etag: readEtag(response.headers),
    };
  }
  const data: ErrorBody = response.data ?? {};
  return {
    kind: failureKind(response.status),
    httpStatus: response.status as 400 | 403 | 404 | 409 | 412 | 428,
    errorCode: data.error ?? 'dispatch_recovery_failed',
    detail: data.detail ?? null,
    recoveryAuditId: data.recoveryAuditId ?? null,
    liveSender: data.liveSender ?? null,
  };
}

export async function getDispatchRecoveryAudit(
  printerId: string,
  auditId: string
): Promise<DispatchRecoveryAudit> {
  const response = await client.get<DispatchRecoveryAudit>(
    `${DISPATCH_API_BASE}/${encodeURIComponent(printerId)}/reconciliation/audit/${encodeURIComponent(auditId)}`
  );
  return response.data;
}

export async function clearDispatchRecoveryBlock(input: {
  jobId: string;
  jobETag: string | null | undefined;
}): Promise<DispatchRecoveryClearResult> {
  const response = await client.post<ErrorBody & { jobId?: string }>(
    `${DISPATCH_API_BASE}/jobs/${encodeURIComponent(input.jobId)}/recovery/clear`,
    undefined,
    {
      headers: { 'If-Match': quotedEtag(input.jobETag, 'The reviewed job') },
      validateStatus: (status) => HANDLED_STATUSES.includes(status),
    }
  );
  if (response.status === 200) {
    return {
      kind: 'cleared',
      httpStatus: 200,
      jobId: response.data?.jobId ?? input.jobId,
      etag: readEtag(response.headers),
    };
  }
  const data: ErrorBody = response.data ?? {};
  return {
    kind: failureKind(response.status),
    httpStatus: response.status as 400 | 403 | 404 | 409 | 412 | 428,
    errorCode: data.error ?? 'dispatch_recovery_clear_failed',
    detail: data.detail ?? null,
  };
}
