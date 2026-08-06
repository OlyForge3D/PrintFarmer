/**
 * Printer-discovery availability query (#1146 item 7). Replaces the previous
 * page-level raw `setInterval` + local `useState` polling loop with one
 * shared TanStack Query hook, enabled only for authorized admins (the only
 * users who can see the "Discover Printers" action). Visibility/background
 * behavior and retry/backoff intentionally fall back to the app-wide
 * defaults in `services/queryClient.ts` (no client-4xx retries, exponential
 * backoff otherwise; `refetchIntervalInBackground` left `false` so a
 * backgrounded tab stops polling and `refetchOnWindowFocus` left `true` so
 * returning to the tab revalidates), matching local convention (e.g.
 * `useSettingsMetadata`, the fleet filament-coverage hooks).
 */
import { useEffect, useState } from 'react';
import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import { useAuth } from '@/features/auth/hooks/useAuth';
import type { NetworkDiscoverySettings } from '@/types/NetworkDiscoverySettings';

export const DISCOVERY_AVAILABILITY_QUERY_KEY = ['network-discovery', 'availability'] as const;
const DISCOVERY_AVAILABILITY_STALE_MS = 15_000;
const DISCOVERY_AVAILABILITY_REFETCH_MS = 30_000;
/** How recent `lastHeartbeat` must be for discovery to be considered available; matches the previous inline check. */
const HEARTBEAT_FRESHNESS_MS = 60_000;
/** How often the freshness check itself re-evaluates against the current time. */
const FRESHNESS_TICK_MS = 15_000;

async function fetchNetworkDiscoverySettings(): Promise<NetworkDiscoverySettings> {
  return apiClient.getSettings<NetworkDiscoverySettings>('NetworkDiscovery');
}

/**
 * Raw discovery-settings query, enabled only when the caller is an admin.
 * Exported mainly for testing; most callers should use `useDiscoveryAvailable`.
 */
export function useNetworkDiscoverySettings(): UseQueryResult<NetworkDiscoverySettings> {
  const { hasPermission } = useAuth();
  const isAdmin = hasPermission('printers', 'admin');

  return useQuery({
    queryKey: DISCOVERY_AVAILABILITY_QUERY_KEY,
    queryFn: fetchNetworkDiscoverySettings,
    enabled: isAdmin,
    staleTime: DISCOVERY_AVAILABILITY_STALE_MS,
    refetchInterval: DISCOVERY_AVAILABILITY_REFETCH_MS,
  });
}

/**
 * Whether printer discovery is currently available: the feature is enabled
 * AND the discovery service's last heartbeat is recent. Freshness is
 * evaluated against a periodically-ticked `now` value (matching the
 * `EstimatedCompletionBadge`/`PrintersPage` availability-filter convention
 * of `useState(Date.now)` + an interval effect) rather than calling
 * `Date.now()` directly during render — `Date.now()` is impure, and
 * React's rules-of-hooks/purity lint forbids calling it in the render body.
 * Because `now` is real component state driven by its own timer, a page left
 * open across a stale or backgrounded query still re-evaluates freshness on
 * its own schedule and cannot keep reporting `true` forever once the
 * heartbeat has actually expired.
 */
export function useDiscoveryAvailable(): boolean {
  const { data } = useNetworkDiscoverySettings();
  const [now, setNow] = useState(Date.now);

  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), FRESHNESS_TICK_MS);
    return () => clearInterval(id);
  }, []);

  if (!data?.enableDiscovery || !data.lastHeartbeat) {
    return false;
  }
  return now - new Date(data.lastHeartbeat).getTime() < HEARTBEAT_FRESHNESS_MS;
}