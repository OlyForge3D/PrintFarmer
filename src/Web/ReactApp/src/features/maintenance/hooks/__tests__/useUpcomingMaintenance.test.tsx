import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useUpcomingMaintenance } from '../useUpcomingMaintenance';

// -----------------------------------------------------------------------------
// Wire-boundary regression fixture for the #711 backend contract.
//
// This payload mirrors `src/api/Controllers/Responses/UpcomingMaintenanceTaskDto.cs`
// as it ships at feature-head `1b696b954`:
//
//   public record UpcomingMaintenanceTaskDto(
//       string Id,
//       Guid TaskId,
//       Guid PrinterId,
//       string PrinterName,
//       string TaskName,
//       string? Component,
//       string? Description,
//       int Priority,
//       string IntervalType,
//       double IntervalValue,
//       DateTime? DueDate,
//       int? DaysUntilDue,
//       double? HoursUntilDue,
//       bool IsOverdue,
//       bool IsDueToday,
//       DateTime? LastPerformedAt,
//       Guid? ToolheadId = null);
//
// The important invariant this file locks in: the wire field is `taskId`
// (a Guid keyed to the global maintenance-task catalog). Before the fix the
// hook mapped `t.scheduleId`, which does not exist on the DTO, so every
// task's identifier was silently `undefined`. Any test that reads
// `task.taskId` off the mapped result would have failed under the old code.
// -----------------------------------------------------------------------------

vi.mock('@/services/maintenanceService', () => ({
  maintenanceService: {
    getUpcomingMaintenance: vi.fn(),
  },
}));

import { maintenanceService } from '@/services/maintenanceService';

const TASK_ID_OVERDUE = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
const TASK_ID_DUE_TODAY = '3f2504e0-4f89-11d3-9a0c-0305e82c3302';
const TASK_ID_UPCOMING = '3f2504e0-4f89-11d3-9a0c-0305e82c3303';
const TASK_ID_HOURS = '3f2504e0-4f89-11d3-9a0c-0305e82c3304';
const TOOLHEAD_ID = '5c1a7c3a-9111-4a1a-8000-000000000001';

/** Realistic wire payload matching the backend C# record shape (camelCase). */
const wirePayload = [
  {
    id: 'u-overdue',
    taskId: TASK_ID_OVERDUE,
    printerId: 'p-1',
    printerName: 'Voron 2.4',
    toolheadId: TOOLHEAD_ID,
    taskName: 'Nozzle change',
    component: 'nozzle',
    description: 'Replace worn nozzle',
    priority: 3,
    intervalType: 'days' as const,
    intervalValue: 30,
    dueDate: '2026-07-10T00:00:00Z',
    daysUntilDue: -3,
    hoursUntilDue: null,
    isOverdue: true,
    isDueToday: false,
    lastPerformedAt: '2026-05-01T00:00:00Z',
  },
  {
    id: 'u-today',
    taskId: TASK_ID_DUE_TODAY,
    printerId: 'p-1',
    printerName: 'Voron 2.4',
    toolheadId: null,
    taskName: 'Bed level check',
    component: null,
    description: null,
    priority: 2,
    intervalType: 'days' as const,
    intervalValue: 7,
    dueDate: '2026-07-13T00:00:00Z',
    daysUntilDue: 0,
    hoursUntilDue: null,
    isOverdue: false,
    isDueToday: true,
    lastPerformedAt: null,
  },
  {
    id: 'u-upcoming',
    taskId: TASK_ID_UPCOMING,
    printerId: 'p-1',
    printerName: 'Voron 2.4',
    toolheadId: null,
    taskName: 'Belt tension',
    component: 'belt',
    description: null,
    priority: 1,
    intervalType: 'days' as const,
    intervalValue: 90,
    dueDate: '2026-07-20T00:00:00Z',
    daysUntilDue: 7,
    hoursUntilDue: null,
    isOverdue: false,
    isDueToday: false,
    lastPerformedAt: '2026-04-20T00:00:00Z',
  },
  {
    id: 'u-hours',
    taskId: TASK_ID_HOURS,
    printerId: 'p-1',
    printerName: 'Voron 2.4',
    toolheadId: null,
    taskName: 'Lubricate rails',
    component: 'linear rails',
    description: null,
    priority: 2,
    intervalType: 'hours' as const,
    intervalValue: 250,
    dueDate: null,
    daysUntilDue: null,
    hoursUntilDue: 40,
    isOverdue: false,
    isDueToday: false,
    lastPerformedAt: null,
  },
];

function makeWrapper(client: QueryClient) {
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
}

function makeClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
}

describe('useUpcomingMaintenance — wire boundary', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('preserves the backend `taskId` on every mapped task (regression: mapping used to read `scheduleId` and produced undefined)', async () => {
    (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(wirePayload);

    const { result } = renderHook(() => useUpcomingMaintenance(), {
      wrapper: makeWrapper(makeClient()),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    // The mapped array must have one entry per wire row, with `taskId` copied
    // through verbatim from the backend DTO. Under the old mapping
    // (`scheduleId: t.scheduleId`), every value here would have been
    // `undefined` because the wire DTO carries `taskId`, not `scheduleId`.
    expect(result.current.tasks).toHaveLength(wirePayload.length);
    const byId = new Map(result.current.tasks.map(t => [t.id, t]));
    expect(byId.get('u-overdue')?.taskId).toBe(TASK_ID_OVERDUE);
    expect(byId.get('u-today')?.taskId).toBe(TASK_ID_DUE_TODAY);
    expect(byId.get('u-upcoming')?.taskId).toBe(TASK_ID_UPCOMING);
    expect(byId.get('u-hours')?.taskId).toBe(TASK_ID_HOURS);

    // Explicit "no undefined" check — the exact runtime symptom of the
    // fixed bug was permanently-undefined identifiers.
    for (const task of result.current.tasks) {
      expect(typeof task.taskId).toBe('string');
      expect(task.taskId).not.toHaveLength(0);
    }
  });

  it('passes toolheadId through unchanged and defaults missing scope to null', async () => {
    (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(wirePayload);

    const { result } = renderHook(() => useUpcomingMaintenance(), {
      wrapper: makeWrapper(makeClient()),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));
    const byId = new Map(result.current.tasks.map(t => [t.id, t]));
    expect(byId.get('u-overdue')?.toolheadId).toBe(TOOLHEAD_ID);
    expect(byId.get('u-today')?.toolheadId).toBeNull();
    expect(byId.get('u-hours')?.toolheadId).toBeNull();
  });

  it('surfaces backend errors instead of returning stale/empty data', async () => {
    (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('backend down'));

    const { result } = renderHook(() => useUpcomingMaintenance(), {
      wrapper: makeWrapper(makeClient()),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.error).toBeInstanceOf(Error);
    expect(result.current.error?.message).toBe('backend down');
    // Empty task list on error — never a false "all-clear" from stale data.
    expect(result.current.tasks).toHaveLength(0);
    expect(result.current.overdueCount).toBe(0);
    expect(result.current.dueSoonCount).toBe(0);
  });

  it('scopes the query cache by printerId so per-printer views do not collide', async () => {
    const captured: unknown[] = [];
    (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockImplementation(async (opts: unknown) => {
      captured.push(opts);
      return [];
    });

    const client = makeClient();

    // First render: scoped to printer A.
    const { result: rA, unmount: unmountA } = renderHook(
      () => useUpcomingMaintenance({ printerId: 'printer-a' }),
      { wrapper: makeWrapper(client) },
    );
    await waitFor(() => expect(rA.current.isLoading).toBe(false));
    unmountA();

    // Second render: scoped to printer B. If the cache key ignored
    // `printerId`, the second query would reuse the printer-A result and
    // never call the service.
    const { result: rB } = renderHook(
      () => useUpcomingMaintenance({ printerId: 'printer-b' }),
      { wrapper: makeWrapper(client) },
    );
    await waitFor(() => expect(rB.current.isLoading).toBe(false));

    expect(captured).toHaveLength(2);
    expect(captured[0]).toMatchObject({ printerId: 'printer-a' });
    expect(captured[1]).toMatchObject({ printerId: 'printer-b' });
  });

  it('sorts overdue tasks ahead of due-today and remaining upcoming tasks', async () => {
    (maintenanceService.getUpcomingMaintenance as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(wirePayload);

    const { result } = renderHook(() => useUpcomingMaintenance(), {
      wrapper: makeWrapper(makeClient()),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.tasks[0].id).toBe('u-overdue');
    // Overdue count and due-soon count should match the fixture.
    expect(result.current.overdueCount).toBe(1);
    // 'u-today', 'u-upcoming' (7d) and 'u-hours' (40h < 7d) are all
    // within the "due soon" window; overdue does not count.
    expect(result.current.dueSoonCount).toBe(3);
  });
});