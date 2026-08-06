/**
 * Batched printer-tags fleet query (#1146 item 1). Mirrors the established
 * fleet filament-coverage pattern (`useFleetFilamentCoverage` /
 * `usePrinterCoverageFromFleet`, issue #717): one call to
 * `GET /api/tags/objects?objectType=Printer` powers every compact printer
 * card's tag list via a `select`-derived, per-printer view, replacing the
 * previous per-card `GET /api/tags/object/{id}?objectType=Printer` fan-out.
 */
import { useCallback } from 'react';
import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiClient, type ObjectTagsDto } from '@/services/api';
import type { TagDto } from '@/services/tagService';

/** 5 minutes — matches the previous per-card tag query's staleTime. */
const PRINTER_TAGS_FLEET_STALE_MS = 5 * 60_000;

export const printerTagsFleetQueryKey = ['printer-tags', 'fleet'] as const;

async function fetchFleetPrinterTags(signal?: AbortSignal): Promise<ObjectTagsDto[]> {
  return apiClient.getObjectsTags('Printer', signal);
}

/**
 * Fleet query. Call this once (e.g. at the page level) to prime the cache so
 * every card's `select`-based read below dedupes onto a single request.
 */
export function useFleetPrinterTags(): UseQueryResult<ObjectTagsDto[]> {
  return useQuery({
    queryKey: printerTagsFleetQueryKey,
    queryFn: ({ signal }) => fetchFleetPrinterTags(signal),
    staleTime: PRINTER_TAGS_FLEET_STALE_MS,
  });
}

/**
 * Per-printer selector for grid surfaces. Every card shares the same fleet
 * query key, so concurrent cards produce one deduplicated request instead of
 * N. Returns an empty array (not `undefined`) once the fleet data has
 * loaded and the printer has no tags, matching the previous per-card
 * query's empty-tag behavior.
 */
export function usePrinterTagsFromFleet(
  printerId: string,
): Pick<UseQueryResult<TagDto[]>, 'data' | 'isPending' | 'isError' | 'error'> {
  const select = useCallback(
    (entries: ObjectTagsDto[]): TagDto[] =>
      entries.find((entry) => entry.objectId === printerId)?.tags ?? [],
    [printerId],
  );
  const { data, isPending, isError, error } = useQuery({
    queryKey: printerTagsFleetQueryKey,
    queryFn: ({ signal }) => fetchFleetPrinterTags(signal),
    staleTime: PRINTER_TAGS_FLEET_STALE_MS,
    select,
  });

  return { data, isPending, isError, error };
}