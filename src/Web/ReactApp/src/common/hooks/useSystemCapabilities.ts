import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { SystemCapabilities } from '@/types/api';

const SYSTEM_CAPABILITIES_KEY = ['system-capabilities'];

/**
 * Fetches platform capabilities once and caches forever.
 * Capabilities (architecture, feature flags) do not change at runtime.
 */
export function useSystemCapabilities() {
  return useQuery<SystemCapabilities>({
    queryKey: SYSTEM_CAPABILITIES_KEY,
    queryFn: () => apiClient.getSystemCapabilities(),
    staleTime: Infinity,
    gcTime: Infinity,
    retry: 2,
  });
}
