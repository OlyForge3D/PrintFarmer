/**
 * Batched printer queue-summary fleet query (#1146 item 9). One call to
 * `GET /api/job-queue-analytics/printer-summaries` powers every compact
 * printer card's "X of Y" queue label via a `select`-derived per-printer
 * view, replacing the previous per-card `useJobQueue(printer.id)` polling
 * and its active-only `enabled` predicate. Mirrors the fleet
 * filament-coverage pattern (`useFleetFilamentCoverage` /
 * `usePrinterCoverageFromFleet`, issue #717).
 *
 * Printers with no active (queued or printing) job are simply absent from
 * the server response, so "no summary entry" is itself the authoritative
 * signal that a printer has nothing to show — no client-side staleness
 * gating is required to avoid a stray label from cached data.
 */
import { useCallback } from 'react';
import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { PrinterQueueSummaryDto } from '@/types/api';

/** Matches the previous per-card job-queue polling cadence (30s refetch). */
const QUEUE_SUMMARIES_STALE_MS = 15_000;
const QUEUE_SUMMARIES_REFETCH_MS = 30_000;

export const queueSummariesFleetQueryKey = ['queue-summaries', 'fleet'] as const;

async function fetchFleetQueueSummaries(signal?: AbortSignal): Promise<PrinterQueueSummaryDto[]> {
  return apiClient.getPrinterQueueSummaries(signal);
}

/**
 * Fleet query. Call this once (e.g. at the page level) to prime the cache so
 * every card's `select`-based read below dedupes onto a single polled
 * request instead of one query per card.
 */
export function useFleetQueueSummaries(): UseQueryResult<PrinterQueueSummaryDto[]> {
  return useQuery({
    queryKey: queueSummariesFleetQueryKey,
    queryFn: ({ signal }) => fetchFleetQueueSummaries(signal),
    staleTime: QUEUE_SUMMARIES_STALE_MS,
    refetchInterval: QUEUE_SUMMARIES_REFETCH_MS,
  });
}

/**
 * Per-printer selector for grid surfaces. Every card shares the same fleet
 * query key, so concurrent cards produce one deduplicated polling request
 * instead of N. Returns `undefined` when the printer has no active job
 * (idle/offline), which callers should treat as "no queue label".
 */
export function useQueueSummaryFromFleet(
  printerId: string,
): Pick<UseQueryResult<PrinterQueueSummaryDto | undefined>, 'data' | 'isPending' | 'isError' | 'error'> {
  const select = useCallback(
    (summaries: PrinterQueueSummaryDto[]): PrinterQueueSummaryDto | undefined =>
      summaries.find((summary) => summary.printerId === printerId),
    [printerId],
  );
  const { data, isPending, isError, error } = useQuery({
    queryKey: queueSummariesFleetQueryKey,
    queryFn: ({ signal }) => fetchFleetQueueSummaries(signal),
    staleTime: QUEUE_SUMMARIES_STALE_MS,
    refetchInterval: QUEUE_SUMMARIES_REFETCH_MS,
    select,
  });

  return { data, isPending, isError, error };
}