import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';

/**
 * Checks whether Spoolman is configured (BaseUrl app setting is set)
 * and healthy (service is reachable). Uses the settings API to read
 * configuration and the Spoolman health endpoint to verify connectivity.
 *
 * @returns `ready` — true when Spoolman is both configured and healthy
 */
export function useSpoolmanConfigured() {
  const { data: config } = useQuery({
    queryKey: ['spoolman-config'],
    queryFn: async () => {
      const result = await apiClient.getSpoolmanConfig();
      return result as { baseUrl?: string | null };
    },
    staleTime: 300_000,
    retry: false,
  });

  const configured = !!config?.baseUrl?.trim();

  const { data: health } = useQuery({
    queryKey: ['spoolman-health'],
    queryFn: async () => {
      const result = await apiClient.getSpoolmanHealth();
      return result as { success?: boolean; configured?: boolean };
    },
    staleTime: 60_000,
    retry: false,
    enabled: configured,
  });

  return {
    configured,
    healthy: !!health?.success,
    ready: configured && !!health?.success,
  };
}
