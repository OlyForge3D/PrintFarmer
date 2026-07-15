/**
 * useScheduleDeployments — cross-cache invalidation tests (Hicks #2).
 *
 * The upcoming-maintenance feed (`useUpcomingMaintenance`) derives its
 * results from the same underlying schedule-deployment rows that these
 * mutations write to. Failing to invalidate the feed after a mutation
 * leaves operators looking at a stale "next due" list until the polling
 * interval (default 120s) fires — a gap in which a just-deployed plan
 * appears absent, a just-updated interval appears wrong, and a just-
 * undeployed task still appears on the roster.
 *
 * These tests exercise the concrete cache — a QueryClient with real
 * observers subscribed to `['upcoming-maintenance', ...]` — and prove
 * that all three mutations invalidate the correct prefix. They do NOT
 * mock invalidateQueries (which would tempt spy-only assertions that
 * cannot detect a wrong query key). Instead, we observe that a query
 * seeded fresh in the cache is dropped to a stale state after each
 * mutation completes.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import {
  useDeployPlan,
  useUpdateScheduleDeployment,
  useDeleteScheduleDeployment,
  scheduleKeys,
} from '../useScheduleDeployments';
import { maintenanceQueryKeys } from '../../queryKeys';

vi.mock('@/services/maintenancePlanService', () => ({
  maintenancePlanService: {
    deployPlan: vi.fn(),
    updateScheduleDeployment: vi.fn(),
    deleteScheduleDeployment: vi.fn(),
  },
}));

import { maintenancePlanService } from '@/services/maintenancePlanService';

function wrapper(qc: QueryClient) {
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );
}

function makeQc(): QueryClient {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: 60_000 } },
  });
}

/**
 * Seed a fresh (non-stale) query in the cache. `setQueryData` timestamps
 * `dataUpdatedAt: Date.now()`, so with `staleTime: 60_000` on the client
 * the query is not stale until either the timer elapses OR an
 * invalidation forces the state.
 */
function seedFresh<T>(qc: QueryClient, key: readonly unknown[], data: T) {
  qc.setQueryData(key, data);
}

describe('useScheduleDeployments — cross-invalidation of upcoming-maintenance (Hicks #2)', () => {
  beforeEach(() => vi.clearAllMocks());

  it('useDeployPlan invalidates BOTH scheduleDeployments and upcoming-maintenance prefixes on success', async () => {
    (maintenancePlanService.deployPlan as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: 'sched-new',
    });
    const qc = makeQc();

    // Seed both prefixes as fresh. Any variant of the upcoming-maintenance
    // key must be reached by the invalidation — react-query treats the
    // provided array as a prefix by default. We seed several variants
    // (with different filter objects) so a "shallow match" bug on the
    // hook implementation would be caught here.
    seedFresh(qc, [...scheduleKeys.list()], ['seed']);
    seedFresh(qc, maintenanceQueryKeys.upcomingMaintenance(), ['seed-a']);
    seedFresh(
      qc,
      maintenanceQueryKeys.upcomingMaintenance({ lookaheadDays: 30, includeOverdue: true, printerId: undefined }),
      ['seed-b'],
    );
    seedFresh(
      qc,
      maintenanceQueryKeys.upcomingMaintenance({ lookaheadDays: 7, includeOverdue: false, printerId: 'printer-1' }),
      ['seed-c'],
    );

    const { result } = renderHook(() => useDeployPlan(), { wrapper: wrapper(qc) });

    await act(async () => {
      await result.current.mutateAsync({
        maintenancePlanId: 'plan-1',
        printerId: 'printer-1',
        toolheadId: null,
        notes: null,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Every prefix variant we seeded must now be stale.
    expect(qc.getQueryState([...scheduleKeys.list()])!.isInvalidated).toBe(true);
    expect(qc.getQueryState(maintenanceQueryKeys.upcomingMaintenance())!.isInvalidated).toBe(true);
    expect(
      qc.getQueryState(
        maintenanceQueryKeys.upcomingMaintenance({ lookaheadDays: 30, includeOverdue: true, printerId: undefined })
      )!.isInvalidated,
    ).toBe(true);
    expect(
      qc.getQueryState(
        maintenanceQueryKeys.upcomingMaintenance({ lookaheadDays: 7, includeOverdue: false, printerId: 'printer-1' })
      )!.isInvalidated,
    ).toBe(true);
  });

  it('useUpdateScheduleDeployment invalidates BOTH prefixes on success', async () => {
    (maintenancePlanService.updateScheduleDeployment as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: 'sched-1',
    });
    const qc = makeQc();
    seedFresh(qc, [...scheduleKeys.list()], ['seed']);
    seedFresh(qc, maintenanceQueryKeys.upcomingMaintenance(), ['seed']);
    seedFresh(
      qc,
      maintenanceQueryKeys.upcomingMaintenance({ lookaheadDays: 30, includeOverdue: true, printerId: 'printer-1' }),
      ['seed-scoped'],
    );

    const { result } = renderHook(() => useUpdateScheduleDeployment(), { wrapper: wrapper(qc) });

    await act(async () => {
      await result.current.mutateAsync({
        id: 'sched-1',
        data: { intervalValue: 60, isActive: true, notes: 'tuned' },
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(qc.getQueryState([...scheduleKeys.list()])!.isInvalidated).toBe(true);
    expect(qc.getQueryState(maintenanceQueryKeys.upcomingMaintenance())!.isInvalidated).toBe(true);
    expect(
      qc.getQueryState(
        maintenanceQueryKeys.upcomingMaintenance({
          lookaheadDays: 30,
          includeOverdue: true,
          printerId: 'printer-1',
        })
      )!.isInvalidated
    ).toBe(true);
  });

  it('useDeleteScheduleDeployment invalidates BOTH prefixes on success (undeploy path)', async () => {
    (maintenancePlanService.deleteScheduleDeployment as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    const qc = makeQc();
    seedFresh(qc, [...scheduleKeys.list()], ['seed']);
    seedFresh(qc, maintenanceQueryKeys.upcomingMaintenance(), ['seed']);
    seedFresh(
      qc,
      maintenanceQueryKeys.upcomingMaintenance({ lookaheadDays: 14, includeOverdue: true, printerId: 'printer-42' }),
      ['seed-scoped'],
    );

    const { result } = renderHook(() => useDeleteScheduleDeployment(), { wrapper: wrapper(qc) });

    await act(async () => {
      await result.current.mutateAsync('sched-to-delete');
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Delete/undeploy must reach the same set of prefixes; without the
    // fix, the operator's "upcoming" roster would still show the
    // deleted plan for up to 120 seconds (the poll interval).
    expect(qc.getQueryState([...scheduleKeys.list()])!.isInvalidated).toBe(true);
    expect(qc.getQueryState(maintenanceQueryKeys.upcomingMaintenance())!.isInvalidated).toBe(true);
    expect(
      qc.getQueryState(
        maintenanceQueryKeys.upcomingMaintenance({ lookaheadDays: 14, includeOverdue: true, printerId: 'printer-42' })
      )!.isInvalidated,
    ).toBe(true);
  });
});
