import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { ProfileTypeSchemaDto } from '@/types/api';

export function useProfileSchema(profileType: 'process' | 'machine' | 'filament') {
  return useQuery({
    queryKey: ['profile-schema', profileType],
    queryFn: () => apiClient.getProfileSchemas(),
    staleTime: 600_000, // 10 min — schema rarely changes
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
