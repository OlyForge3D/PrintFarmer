import { useContext } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { QueryClient } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import { AuthContext } from '@/common/contexts/auth-context';
import { apiClient } from '@/services/api';
import {
  clearDispatchRecoveryBlock,
  getDispatchReconciliation,
  getDispatchRecoveryAudit,
  recoverDispatchClaim,
} from '@/services/api/dispatchRecoveryApi';
import type {
  DispatchRecoveryClearResult,
  DispatchRecoveryRequest,
  DispatchRecoveryResult,
  QueuedPrintJobWithFileMetaDto,
} from '@/types/api';

export const dispatchRecoveryKeys = {
  all: ['dispatch-reconciliation'] as const,
  reconciliation: (printerId: string) =>
    ['dispatch-reconciliation', printerId] as const,
  audit: (printerId: string, auditId: string) =>
    ['dispatch-reconciliation', printerId, 'audit', auditId] as const,
};

const RECONCILIATION_STALE_TIME_MS = 15_000;
/** Polls only while a claim is indeterminate; SignalR invalidation covers the idle case. */
const RECONCILIATION_REFETCH_INTERVAL_MS = 30_000;

function isAccessDenied(error: unknown): boolean {
  return (
    isAxiosError(error) &&
    (error.response?.status === 403 || error.response?.status === 404)
  );
}

export function useDispatchReconciliation(
  printerId: string | null | undefined,
  options: { enabled?: boolean } = {}
) {
  return useQuery({
    queryKey: dispatchRecoveryKeys.reconciliation(printerId ?? ''),
    queryFn: () => getDispatchReconciliation(printerId as string),
    enabled: Boolean(printerId) && (options.enabled ?? true),
    staleTime: RECONCILIATION_STALE_TIME_MS,
    refetchInterval: (query) =>
      query.state.data?.resource.hasIndeterminateClaim
        ? RECONCILIATION_REFETCH_INTERVAL_MS
        : false,
    retry: (failureCount, error) => !isAccessDenied(error) && failureCount < 2,
  });
}

export function useDispatchRecoveryAudit(
  printerId: string,
  auditId: string | null | undefined,
  options: { enabled?: boolean } = {}
) {
  return useQuery({
    queryKey: dispatchRecoveryKeys.audit(printerId, auditId ?? ''),
    queryFn: () => getDispatchRecoveryAudit(printerId, auditId as string),
    enabled: Boolean(auditId) && (options.enabled ?? true),
    staleTime: Infinity,
    retry: (failureCount, error) => !isAccessDenied(error) && failureCount < 1,
  });
}

function invalidateDispatchRecoveryState(
  qc: QueryClient,
  printerId?: string | null
): Promise<unknown> {
  return Promise.all([
    qc.invalidateQueries({
      queryKey: printerId
        ? dispatchRecoveryKeys.reconciliation(printerId)
        : dispatchRecoveryKeys.all,
    }),
    qc.invalidateQueries({ queryKey: ['queue-jobs'] }),
    qc.invalidateQueries({ queryKey: ['queue-stats'] }),
    qc.invalidateQueries({ queryKey: ['job-queue'] }),
  ]);
}

/**
 * Fail-closed recovery mutation: no optimistic update. Every settled response
 * (success or rejection) invalidates the reconciliation resource so the UI
 * re-derives state from the server.
 */
export function useRecoverDispatchClaim() {
  const qc = useQueryClient();
  return useMutation<
    DispatchRecoveryResult,
    unknown,
    {
      printerId: string;
      etag: string;
      idempotencyKey: string;
      body: DispatchRecoveryRequest;
    }
  >({
    mutationFn: recoverDispatchClaim,
    onSettled: (_data, _error, variables) =>
      invalidateDispatchRecoveryState(qc, variables.printerId),
  });
}

export function useClearDispatchRecoveryBlock() {
  const qc = useQueryClient();
  return useMutation<
    DispatchRecoveryClearResult,
    unknown,
    { jobId: string; jobETag: string | null | undefined; printerId?: string | null }
  >({
    mutationFn: ({ jobId, jobETag }) => clearDispatchRecoveryBlock({ jobId, jobETag }),
    onSettled: (_data, _error, variables) =>
      invalidateDispatchRecoveryState(qc, variables.printerId),
  });
}

const CANDIDATE_PAGE_SIZE = 1000;
const CANDIDATE_MAX_PAGES = 20;

/**
 * Unfiltered, fully paged active-queue read used only to discover dispatch
 * recovery candidates, so dashboard filters and table pagination can never
 * hide an indeterminate claim or a recovery-held job. Keyed under
 * `queue-jobs` so every queue invalidation refreshes it.
 */
export function useDispatchRecoveryCandidates(options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: ['queue-jobs', 'dispatch-recovery-candidates'] as const,
    queryFn: async () => {
      const all: QueuedPrintJobWithFileMetaDto[] = [];
      for (let page = 0; page < CANDIDATE_MAX_PAGES; page += 1) {
        const batch = (await apiClient.getAnalyticsQueueJobs(
          undefined,
          undefined,
          undefined,
          'priority',
          CANDIDATE_PAGE_SIZE,
          page * CANDIDATE_PAGE_SIZE
        )) as QueuedPrintJobWithFileMetaDto[];
        all.push(...batch);
        if (batch.length < CANDIDATE_PAGE_SIZE) {
          break;
        }
      }
      return all;
    },
    enabled: options.enabled ?? true,
    staleTime: RECONCILIATION_STALE_TIME_MS,
    refetchInterval: RECONCILIATION_STALE_TIME_MS,
  });
}

/**
 * `queue:reconcile` check that fails closed (false) when no auth context is
 * mounted, so embedding surfaces never grant recovery actions by accident.
 */
export function useCanReconcileQueue(): boolean {
  const auth = useContext(AuthContext);
  return auth?.hasPermission('queue', 'reconcile') ?? false;
}
