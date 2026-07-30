import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { ProfileTypeSchemaDto } from '@/types/api';

/**
 * Fetches version-scoped schema metadata for a profile type (issue #578).
 *
 * The queryKey includes `engineVersion` so switching the pinned OrcaSlicer engine
 * on the New Slice Job page invalidates the schema cache and re-fetches — added
 * fields appear, removed fields disappear, and renamed fields flip to the correct
 * engine-version key without cross-version cache contamination.
 *
 * Pass `undefined` for `engineVersion` to receive the full unfiltered schema
 * (e.g. from Profile management pages that are engine-agnostic).
 */
export function useProfileSchema(
  profileType: 'process' | 'machine' | 'filament',
  engineVersion?: string,
) {
  return useQuery({
    queryKey: ['profile-schema', profileType, engineVersion ?? null],
    queryFn: () => apiClient.getProfileSchemas(engineVersion),
    staleTime: 600_000, // 10 min — schema per (profileType, engineVersion) rarely changes
    select: (data): ProfileTypeSchemaDto => {
      switch (profileType) {
        case 'process':
          return data.process;
        case 'machine':
          return data.machine;
        case 'filament':
          return data.filament;
      }
    },
  });
}
