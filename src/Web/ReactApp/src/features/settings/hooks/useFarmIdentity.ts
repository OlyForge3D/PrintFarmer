import { useQuery } from '@tanstack/react-query';
import { fetchSettingsValues } from '@/services/settingsApi';
import type { FarmIdentitySettings } from '@/types/api';

export const FARM_IDENTITY_QUERY_KEY = ['settings', 'FarmIdentity'] as const;

export function useFarmIdentity(enabled: boolean) {
  return useQuery({
    queryKey: FARM_IDENTITY_QUERY_KEY,
    queryFn: () => fetchSettingsValues<FarmIdentitySettings>('FarmIdentity'),
    enabled,
    staleTime: 30_000,
    refetchInterval: 60_000,
  });
}
