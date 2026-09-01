// Standalone health-check hooks, decoupled from `common/hooks/useApi.ts` (which
// statically imports the `ApiClient` monolith) so the setup wizard — statically
// imported by App.tsx — stays out of that monolith's eager import graph.
// See issue #2343.
import { useQuery } from '@tanstack/react-query';
import type { UseQueryOptions } from '@tanstack/react-query';
import { getHealthStatus, getBasicHealth } from '@/services/api/healthApi';
import type { ApiError, BasicHealthStatus, DetailedHealthStatus, HealthStatus } from '@/types/api';

type QueryOptions<TData, TError = ApiError> = Omit<UseQueryOptions<TData, TError>, 'queryKey' | 'queryFn'>;

export function useHealthStatus(options?: QueryOptions<HealthStatus>) {
  return useQuery({
    queryKey: ['health'],
    queryFn: async () => {
      const raw = (await getHealthStatus()) as unknown; // backend detailed or basic
      if (typeof raw === 'object' && raw !== null) {
        const r = raw as Record<string, unknown>;
        if (typeof r.results === 'object' && r.results !== null) {
          const startup = r.startup && typeof r.startup === 'object' ? r.startup as Record<string, unknown> : undefined;
          return {
            kind: 'detailed',
            status: String(r.status ?? 'unknown'),
            totalChecksDuration: String(r.totalChecksDuration ?? ''),
            startup: startup ? {
              phase: String(startup.phase ?? 'Unknown'),
              ready: Boolean(startup.ready),
              failed: Boolean(startup.failed),
              failureMessage: startup.failureMessage ? String(startup.failureMessage) : undefined,
              failureStackTrace: startup.failureStackTrace ? String(startup.failureStackTrace) : undefined,
              initStartedUtc: startup.initStartedUtc ? String(startup.initStartedUtc) : undefined,
              initCompletedUtc: startup.initCompletedUtc ? String(startup.initCompletedUtc) : undefined,
              initDurationMs: typeof startup.initDurationMs === 'number' ? startup.initDurationMs : undefined,
            } : undefined,
            results: r.results as DetailedHealthStatus['results']
          } satisfies DetailedHealthStatus;
        }
        return { kind: 'basic', status: String(r.status ?? 'unknown') } satisfies BasicHealthStatus;
      }
      return { kind: 'basic', status: 'unknown' } satisfies BasicHealthStatus;
    },
    staleTime: 30000,
    ...options,
  });
}

export function useBasicHealth(options?: QueryOptions<BasicHealthStatus>) {
  return useQuery({
    queryKey: ['health', 'basic'],
    queryFn: async () => {
      const raw = (await getBasicHealth()) as unknown;
      if (typeof raw === 'object' && raw !== null) {
        const r = raw as Record<string, unknown>;
        return { kind: 'basic', status: String(r.status ?? 'unknown') } satisfies BasicHealthStatus;
      }
      return { kind: 'basic', status: 'unknown' } satisfies BasicHealthStatus;
    },
    staleTime: 10000,
    ...options,
  });
}
