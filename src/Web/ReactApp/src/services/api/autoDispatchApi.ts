import { client } from '@/services/api/httpClient';
import type {
  AutoDispatchGlobalStatus,
  AutoDispatchReadyResult,
  AutoDispatchStatus,
  BedClearAcknowledgementResult,
} from '@/types/api';

/**
 * Auto-dispatch API used by `useAutoDispatch.ts`, whose
 * `useAllAutoDispatchStatuses` hook is statically imported by `Layout.tsx`
 * (mounted for every authenticated route). This module calls the shared
 * axios client directly rather than delegating to the `ApiClient` monolith,
 * keeping it out of that monolith's eager import graph. See issue #2343.
 */

const AUTO_DISPATCH_API_BASE = '/auto-dispatch';

function reviewedEtag(value: string, label: string): string {
  const reviewed = value.trim();
  if (!reviewed) {
    throw new Error(`${label} does not have a reviewed ETag`);
  }
  return reviewed.startsWith('"') ? reviewed : `"${reviewed}"`;
}

function autoDispatchIfMatch(dispatchStateETag: string): string {
  const value = dispatchStateETag.trim();
  if (!value) {
    throw new Error('The reviewed auto-dispatch status does not have an ETag');
  }
  return value.startsWith('"') ? value : `"${value}"`;
}

export async function getAutoDispatchStatus(): Promise<AutoDispatchGlobalStatus> {
  const response = await client.get(`${AUTO_DISPATCH_API_BASE}/status`);
  return response.data;
}

export async function confirmAutoDispatchReady(
  printerId: string,
  dispatchStateETag: string,
  confirmFilamentOverride = false,
  overrideJobETag?: string | null,
  filamentCheckETag?: string | null
): Promise<AutoDispatchReadyResult> {
  const etag = autoDispatchIfMatch(dispatchStateETag);
  const overrideQuery = confirmFilamentOverride
    ? '?confirmFilamentOverride=true'
    : '';
  const response = await client.post(
    `${AUTO_DISPATCH_API_BASE}/${printerId}/ready${overrideQuery}`,
    undefined,
    {
      headers: {
        'If-Match': etag,
        ...(confirmFilamentOverride
          ? {
              'X-Job-If-Match': reviewedEtag(
                overrideJobETag as string,
                'The reviewed filament override job'
              ),
              'X-Filament-Check-If-Match': reviewedEtag(
                filamentCheckETag as string,
                'The reviewed filament check'
              ),
            }
          : {}),
      },
      validateStatus: (status) =>
        status === 200 || status === 202 || status === 409,
    }
  );
  if (
    response.status === 409 &&
    (
      (
        response.data?.requiresFilamentOverride !== true &&
        response.data?.filamentCheckChanged !== true
      ) ||
      typeof response.data?.status !== 'object' ||
      response.data?.status === null
    )
  ) {
    const data = response.data as { detail?: string; error?: string } | undefined;
    throw Object.assign(
      new Error(data?.detail ?? data?.error ?? 'The ready request conflicted with the current queue state.'),
      {
        statusCode: response.status,
        data: response.data,
      }
    );
  }
  return response.data;
}

export async function skipAutoDispatchJob(
  printerId: string,
  dispatchStateETag: string,
  jobETag: string
): Promise<void> {
  const etag = autoDispatchIfMatch(dispatchStateETag);
  const jobEtag = reviewedEtag(jobETag, 'The reviewed next job');
  await client.post(
    `${AUTO_DISPATCH_API_BASE}/${printerId}/skip`,
    undefined,
    {
      headers: {
        'If-Match': etag,
        'X-Job-If-Match': jobEtag,
      },
    }
  );
}

export async function cancelAutoDispatch(
  printerId: string,
  dispatchStateETag: string
): Promise<void> {
  const etag = autoDispatchIfMatch(dispatchStateETag);
  await client.post(
    `${AUTO_DISPATCH_API_BASE}/${printerId}/cancel`,
    undefined,
    { headers: { 'If-Match': etag } }
  );
}

export async function setAutoDispatchEnabled(
  printerId: string,
  enabled: boolean,
  dispatchStateETag: string,
  printerETag: string
): Promise<void> {
  const etag = autoDispatchIfMatch(dispatchStateETag);
  const printerEtag = reviewedEtag(printerETag, 'The reviewed printer');
  await client.put(
    `${AUTO_DISPATCH_API_BASE}/${printerId}/enabled`,
    { enabled },
    {
      headers: {
        'If-Match': etag,
        'X-Printer-If-Match': printerEtag,
      },
    }
  );
}

export async function setAutoDispatchGlobalEnabled(
  enabled: boolean,
  statuses: AutoDispatchStatus[]
): Promise<void> {
  const expectedVersions = Object.fromEntries(
    statuses.map((status) => {
      if (!status.dispatchStateETag || !status.printerETag) {
        throw new Error(
          `Printer ${status.printerId} does not have reviewed ETags`
        );
      }
      return [
        status.printerId,
        {
          dispatchStateETag: status.dispatchStateETag,
          printerETag: status.printerETag,
        },
      ];
    })
  );
  await client.put(`${AUTO_DISPATCH_API_BASE}/enabled`, {
    enabled,
    expectedVersions,
  });
}

export async function preClearAutoDispatchBed(
  printerId: string,
  dispatchStateETag: string
): Promise<AutoDispatchStatus> {
  const etag = autoDispatchIfMatch(dispatchStateETag);
  const response = await client.post(
    `${AUTO_DISPATCH_API_BASE}/${printerId}/pre-clear`,
    undefined,
    { headers: { 'If-Match': etag } }
  );
  return response.data;
}

export async function acknowledgeBedClearAndStart(input: {
  jobId: string;
  printerId: string;
  jobETag: string;
  dispatchStateETag: string;
  expectedPrinterConfigRevision?: number | null;
  idempotencyKey: string;
}): Promise<BedClearAcknowledgementResult> {
  const response = await client.post<
    {
      message?: string;
      jobETag?: string | null;
      dispatchStateETag?: string | null;
      error?: string;
      detail?: string;
    }
  >(
    `/job-queue/${input.jobId}/acknowledge-bed-clear-and-start`,
    {
      printerId: input.printerId,
      expectedPrinterConfigRevision:
        input.expectedPrinterConfigRevision ?? null,
    },
    {
      headers: {
        'Idempotency-Key': input.idempotencyKey,
        'If-Match': reviewedEtag(input.jobETag, 'The reviewed job'),
        'X-Dispatch-State-If-Match': reviewedEtag(
          input.dispatchStateETag,
          'The reviewed dispatch state'
        ),
      },
      validateStatus: (status) =>
        [200, 202, 409, 412, 422, 503].includes(status),
    }
  );
  if (response.status === 200 || response.status === 202) {
    return {
      kind: response.status === 202 ? 'accepted' : 'replayed',
      httpStatus: response.status,
      message: response.data.message,
      jobETag: response.data.jobETag,
      dispatchStateETag: response.data.dispatchStateETag,
    };
  }
  return {
    kind:
      response.status === 409
        ? 'conflict'
        : response.status === 412
          ? 'stale'
          : response.status === 422
            ? 'incompatible'
            : 'unavailable',
    httpStatus: response.status as 409 | 412 | 422 | 503,
    errorCode: response.data.error ?? 'bed_clear_acknowledgement_failed',
    detail: response.data.detail,
  };
}
