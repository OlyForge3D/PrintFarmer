/**
 * React Query hook for the printed-parts harvest action (#722).
 */

import { useMutation, useQueryClient, type UseMutationResult } from '@tanstack/react-query';
import type { HarvestJobRequest, HarvestJobResponse } from '@/types/parts-inventory';
import { harvestJob, HarvestServiceError } from '@/services/partsHarvest';

export interface HarvestJobVariables {
  jobId: string;
  request: HarvestJobRequest;
}

/** Query keys invalidated after a successful (or replayed) harvest. */
export const HARVEST_INVALIDATION_KEYS: readonly (readonly string[])[] = [
  ['parts-inventory'],
  ['parts-inventory-adjustments'],
  ['parts-inventory-mappings'],
  ['queue-history'],
  ['queue-history-recent'],
  ['printerHistory'],
  ['attention'],
  ['tasks'],
];

/**
 * Mutation hook wrapping the harvest service. On success, invalidates
 * every query key that may show stale printed-part or harvest state.
 */
export function useHarvestJob(): UseMutationResult<
  HarvestJobResponse,
  HarvestServiceError,
  HarvestJobVariables
> {
  const queryClient = useQueryClient();
  return useMutation<HarvestJobResponse, HarvestServiceError, HarvestJobVariables>({
    mutationFn: ({ jobId, request }) => harvestJob(jobId, request),
    onSuccess: () => {
      for (const key of HARVEST_INVALIDATION_KEYS) {
        void queryClient.invalidateQueries({ queryKey: [...key] });
      }
    },
  });
}
