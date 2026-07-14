/**
 * useUpcomingMaintenance Hook
 * 
 * Calculates upcoming maintenance tasks based on schedules and last performed dates.
 * Provides a timeline view of maintenance activities across the fleet.
 */

import { useQuery } from '@tanstack/react-query';
import { maintenanceService } from '@/services/maintenanceService';

export interface UpcomingMaintenanceTask {
  id: string;
  /**
   * Global maintenance-task catalog id (a Guid on the wire). Named `taskId`
   * to match the backend record `UpcomingMaintenanceTaskDto.TaskId`
   * (`src/api/Controllers/Responses/UpcomingMaintenanceTaskDto.cs`) and the
   * wire type `UpcomingMaintenanceTaskDto.taskId` in
   * `src/Web/ReactApp/src/services/maintenanceService.ts`. This is NOT a
   * `PrinterMaintenanceSchedule` id — the upcoming feed keys upcoming work
   * to the global task catalog, not to a per-printer schedule deployment,
   * so a `scheduleId` alias would misrepresent the contract and produced a
   * silently-`undefined` field at runtime prior to this fix.
   */
  taskId: string;
  printerId: string;
  printerName: string;
  /**
   * Optional physical toolhead scope. `null`/omitted means the task is
   * printer-wide. Populated by the #711 backend on toolhead-scoped
   * schedules so the UI can join upcoming tasks to specific tools.
   *
   * The backend `UpcomingMaintenanceTaskDto` does NOT carry a `ToolheadName`
   * — callers resolve the display name from `PrinterDetailsDto.toolheads[]`,
   * which is the authoritative source. Synthesising a permanently-null
   * `toolheadName` on the client would misrepresent the wire contract.
   */
  toolheadId?: string | null;
  taskName: string;
  component?: string | null;
  description?: string | null;
  priority: number;
  /** Real due date for day-based items; undefined for hour-based items */
  dueDate?: Date;
  /** Days until due (negative = overdue) for day-based items */
  daysUntilDue?: number;
  /** Hours until due (negative/0 = overdue) for hour-based items */
  hoursUntilDue?: number;
  /** Whether the task is overdue */
  isOverdue: boolean;
  /** Whether the task is due today */
  isDueToday: boolean;
  /** Last time this maintenance was performed */
  lastPerformed?: Date;
  /** Interval type */
  intervalType: 'hours' | 'days';
  intervalValue: number;
}

export interface UseUpcomingMaintenanceOptions {
  /** Number of days to look ahead (default: 30) */
  lookaheadDays?: number;
  /** Include overdue tasks (default: true) */
  includeOverdue?: boolean;
  /** Filter by printer ID (optional) */
  printerId?: string;
  /** Polling interval in ms (default: 120000) */
  refetchInterval?: number;
}

export interface UseUpcomingMaintenanceResult {
  /** All upcoming maintenance tasks sorted by due date */
  tasks: UpcomingMaintenanceTask[];
  /** Tasks grouped by date for calendar view */
  tasksByDate: Map<string, UpcomingMaintenanceTask[]>;
  /** Overdue tasks count */
  overdueCount: number;
  /** Tasks due within 7 days */
  dueSoonCount: number;
  /** Loading state */
  isLoading: boolean;
  /** Error state */
  error: Error | null;
  /** Refetch data */
  refetch: () => void;
}

const QUERY_KEY = 'upcoming-maintenance';

/**
 * Hook for calculating upcoming maintenance tasks
 */
export function useUpcomingMaintenance(
  options: UseUpcomingMaintenanceOptions = {}
): UseUpcomingMaintenanceResult {
  const { 
    lookaheadDays = 30, 
    includeOverdue = true, 
    printerId,
    refetchInterval = 120000 
  } = options;

  const {
    data: tasksData,
    isLoading,
    error,
    refetch,
  } = useQuery<UpcomingMaintenanceTask[], Error>({
    queryKey: [QUERY_KEY, { lookaheadDays, includeOverdue, printerId }],
    queryFn: async () => {
      const data = await maintenanceService.getUpcomingMaintenance({
        lookaheadDays,
        includeOverdue,
        printerId,
      });

      return data.map((t) => ({
        id: t.id,
        // Wire boundary: backend `UpcomingMaintenanceTaskDto` uses `taskId`
        // (a Guid keyed to the global `MaintenanceTask` catalog). The prior
        // `t.scheduleId` mapping produced `undefined` at runtime because no
        // such field exists on the DTO. Regression test at
        // `__tests__/useUpcomingMaintenance.test.tsx`.
        taskId: t.taskId,
        printerId: t.printerId,
        printerName: t.printerName,
        // Toolhead scope is a plain GUID pass-through from the #711 wire
        // contract; the wire DTO does not carry a `toolheadName` and we
        // deliberately do not synthesise one here. Consumers resolve the
        // display name from `PrinterDetailsDto.toolheads[]`.
        toolheadId: t.toolheadId ?? null,
        taskName: t.taskName,
        component: t.component,
        description: t.description,
        priority: t.priority,
        intervalType: t.intervalType,
        intervalValue: t.intervalValue,
        dueDate: t.dueDate ? new Date(t.dueDate) : undefined,
        daysUntilDue: t.daysUntilDue ?? undefined,
        hoursUntilDue: t.hoursUntilDue ?? undefined,
        isOverdue: t.isOverdue,
        isDueToday: t.isDueToday,
        lastPerformed: t.lastPerformedAt ? new Date(t.lastPerformedAt) : undefined,
      }));
    },
    refetchInterval,
    staleTime: 60000,
  });

  const tasks = (tasksData ?? []).slice();

  // Sort: overdue first, then by due date (days) or hours remaining (hours)
  tasks.sort((a, b) => {
    if (a.isOverdue && !b.isOverdue) return -1;
    if (!a.isOverdue && b.isOverdue) return 1;

    const aHasDate = Boolean(a.dueDate);
    const bHasDate = Boolean(b.dueDate);
    if (aHasDate && bHasDate) {
      return a.dueDate!.getTime() - b.dueDate!.getTime();
    }
    if (!aHasDate && !bHasDate) {
      return (a.hoursUntilDue ?? Number.POSITIVE_INFINITY) - (b.hoursUntilDue ?? Number.POSITIVE_INFINITY);
    }

    // Prefer real due dates over hour-only items when mixed
    return aHasDate ? -1 : 1;
  });

  // Group by date for calendar view
  const tasksByDate = new Map<string, UpcomingMaintenanceTask[]>();
  for (const task of tasks) {
    if (!task.dueDate) continue;
    const dateKey = task.dueDate.toISOString().split('T')[0]; // YYYY-MM-DD
    const existing = tasksByDate.get(dateKey) || [];
    tasksByDate.set(dateKey, [...existing, task]);
  }

  // Calculate counts
  const overdueCount = tasks.filter(t => t.isOverdue).length;
  const dueSoonCount = tasks.filter(t => {
    if (t.isOverdue) return false;
    if (t.intervalType === 'days') return (t.daysUntilDue ?? Number.POSITIVE_INFINITY) <= 7;
    return (t.hoursUntilDue ?? Number.POSITIVE_INFINITY) <= 24 * 7;
  }).length;

  return {
    tasks,
    tasksByDate,
    overdueCount,
    dueSoonCount,
    isLoading,
    error: error ?? null,
    refetch,
  };
}
