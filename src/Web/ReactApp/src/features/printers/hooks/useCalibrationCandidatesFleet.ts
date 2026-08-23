/**
 * Batched calibration-eligibility fleet query (issue #1923). One call to
 * `GET /api/printers/calibration-candidates` powers every printer card/row's
 * "needs calibration setup" onboarding prompt via a `select`-derived
 * per-printer view, mirroring the established fleet patterns
 * (`useFleetQueueSummaries` / `useQueueSummaryFromFleet`,
 * `useFleetPrinterTags` / `usePrinterTagsFromFleet`) instead of an N+1
 * per-card `getCalibrationContext(printerId)` fan-out.
 */
import { useCallback } from 'react';
import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { CalibrationCandidateDto } from '@/types/api';

/** Calibration eligibility changes only on explicit operator action (profile
 *  binding, manual setup save, firmware re-probe), so a short poll isn't
 *  needed — a 5 minute staleTime matches the printer-tags fleet cadence. */
const CALIBRATION_CANDIDATES_FLEET_STALE_MS = 5 * 60_000;

export const calibrationCandidatesFleetQueryKey = ['calibration-candidates', 'fleet'] as const;

async function fetchFleetCalibrationCandidates(
  signal?: AbortSignal,
): Promise<CalibrationCandidateDto[]> {
  return apiClient.getCalibrationCandidates(signal);
}

/**
 * Fleet query. Call this once (e.g. at the page level) to prime the cache so
 * every card's `select`-based read below dedupes onto a single request.
 */
export function useFleetCalibrationCandidates(): UseQueryResult<CalibrationCandidateDto[]> {
  return useQuery({
    queryKey: calibrationCandidatesFleetQueryKey,
    queryFn: ({ signal }) => fetchFleetCalibrationCandidates(signal),
    staleTime: CALIBRATION_CANDIDATES_FLEET_STALE_MS,
  });
}

/**
 * Per-printer selector for card/row surfaces. Every card shares the same
 * fleet query key, so concurrent cards produce one deduplicated request
 * instead of one per printer. Returns `undefined` while the fleet snapshot
 * is still loading or the printer is absent from the response.
 */
export function useCalibrationCandidateFromFleet(
  printerId: string,
): Pick<UseQueryResult<CalibrationCandidateDto | undefined>, 'data' | 'isPending' | 'isError' | 'error'> {
  const select = useCallback(
    (candidates: CalibrationCandidateDto[]): CalibrationCandidateDto | undefined =>
      candidates.find((candidate) => candidate.id === printerId),
    [printerId],
  );
  const { data, isPending, isError, error } = useQuery({
    queryKey: calibrationCandidatesFleetQueryKey,
    queryFn: ({ signal }) => fetchFleetCalibrationCandidates(signal),
    staleTime: CALIBRATION_CANDIDATES_FLEET_STALE_MS,
    select,
  });

  return { data, isPending, isError, error };
}
