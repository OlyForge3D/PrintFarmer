import { useQuery } from '@tanstack/react-query';
import { client } from '@/services/api/httpClient';
import type { SystemCapabilities } from '@/types/api';

const SYSTEM_CAPABILITIES_KEY = ['system-capabilities'];

/**
 * Fetches platform capabilities once and caches forever.
 * Capabilities (architecture, feature flags) do not change at runtime.
 */
export function useSystemCapabilities() {
  return useQuery<SystemCapabilities>({
    queryKey: SYSTEM_CAPABILITIES_KEY,
    queryFn: async () => {
      const response = await client.get<SystemCapabilities>('/system/capabilities');
      return response.data;
    },
    staleTime: Infinity,
    gcTime: Infinity,
    retry: 2,
  });
}
