/**
 * useUpcomingMaintenance Hook
 * 
 * Calculates upcoming maintenance tasks based on schedules and last performed dates.
 * Provides a timeline view of maintenance activities across the fleet.
 */

import { useQuery } from '@tanstack/react-query';
import { usePrinters } from '@/common/hooks/useApi';
import { maintenanceService } from '@/services/maintenanceService';
import { addDays, differenceInDays, isPast, isToday } from 'date-fns';
import type { MaintenanceSchedule } from '@/types/maintenance';

export interface UpcomingMaintenanceTask {
  id: string;
  scheduleId: string;
  printerId: string;
  printerName: string;
  taskName: string;
  component?: string | null;
  description?: string | null;
  priority: number;
  /** Estimated due date based on interval and last performed */
  dueDate: Date;
  /** Days until due (negative = overdue) */
  daysUntilDue: number;
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

  // Fetch printers
  const { data: printers, isLoading: printersLoading } = usePrinters();

  // Fetch all schedules
  const { 
    data: schedules, 
    isLoading: schedulesLoading,
    error: schedulesError,
    refetch: refetchSchedules
  } = useQuery<MaintenanceSchedule[], Error>({
    queryKey: [QUERY_KEY, 'schedules'],
    queryFn: () => maintenanceService.getAllSchedules(),
    refetchInterval,
    staleTime: 60000,
  });

  // Fetch maintenance logs to determine last performed dates
  // For now, we'll estimate based on schedule creation or current date
  // In a full implementation, we'd fetch logs per printer

  const isLoading = printersLoading || schedulesLoading;

  // Calculate upcoming tasks
  const tasks: UpcomingMaintenanceTask[] = [];
  const now = new Date();

  if (printers && schedules) {
    for (const printer of printers) {
      // Filter by printer if specified
      if (printerId && printer.id !== printerId) continue;

      // Get applicable schedules for this printer
      const applicableSchedules = schedules.filter(schedule => {
        if (!schedule.isActive) return false;
        
        // Schedule applies if:
        // 1. It's printer-specific and matches this printer
        // 2. It's model-specific and matches this printer's model
        // 3. It's a default schedule (applies to all)
        if (schedule.printerId && schedule.printerId === printer.id) return true;
        if (schedule.printerModelId && schedule.printerModelId === printer.modelId) return true;
        if (schedule.isDefault && !schedule.printerId && !schedule.printerModelId) return true;
        
        return false;
      });

      for (const schedule of applicableSchedules) {
        // Calculate due date based on interval
        // For now, estimate from schedule creation date + interval
        // In a real implementation, we'd use actual last maintenance log
        let dueDate: Date;
        let intervalType: 'hours' | 'days';
        let intervalValue: number;

        if (schedule.intervalDays) {
          intervalType = 'days';
          intervalValue = schedule.intervalDays;
          // Estimate: assume last maintenance was at creation, next is interval days later
          dueDate = addDays(new Date(schedule.createdAt), schedule.intervalDays);
          
          // If that's in the past, calculate next occurrence
          while (isPast(dueDate) && !isToday(dueDate)) {
            dueDate = addDays(dueDate, schedule.intervalDays);
          }
        } else if (schedule.intervalHours) {
          intervalType = 'hours';
          intervalValue = schedule.intervalHours;
          // Convert hours to approximate days for calendar
          const intervalDaysApprox = Math.ceil(schedule.intervalHours / 24);
          dueDate = addDays(new Date(schedule.createdAt), intervalDaysApprox);
          
          while (isPast(dueDate) && !isToday(dueDate)) {
            dueDate = addDays(dueDate, intervalDaysApprox);
          }
        } else {
          // No interval defined, skip
          continue;
        }

        const daysUntilDue = differenceInDays(dueDate, now);
        const isOverdue = daysUntilDue < 0;
        const isDueToday = isToday(dueDate);

        // Filter by lookahead window
        if (!includeOverdue && isOverdue) continue;
        if (daysUntilDue > lookaheadDays) continue;

        tasks.push({
          id: `${printer.id}-${schedule.id}`,
          scheduleId: schedule.id,
          printerId: printer.id,
          printerName: printer.name || 'Unknown Printer',
          taskName: schedule.taskName,
          component: schedule.component,
          description: schedule.description,
          priority: schedule.priority,
          dueDate,
          daysUntilDue,
          isOverdue,
          isDueToday,
          intervalType,
          intervalValue,
        });
      }
    }
  }

  // Sort by due date (overdue first, then soonest)
  tasks.sort((a, b) => {
    // Overdue items first
    if (a.isOverdue && !b.isOverdue) return -1;
    if (!a.isOverdue && b.isOverdue) return 1;
    // Then by due date
    return a.dueDate.getTime() - b.dueDate.getTime();
  });

  // Group by date for calendar view
  const tasksByDate = new Map<string, UpcomingMaintenanceTask[]>();
  for (const task of tasks) {
    const dateKey = task.dueDate.toISOString().split('T')[0]; // YYYY-MM-DD
    const existing = tasksByDate.get(dateKey) || [];
    tasksByDate.set(dateKey, [...existing, task]);
  }

  // Calculate counts
  const overdueCount = tasks.filter(t => t.isOverdue).length;
  const dueSoonCount = tasks.filter(t => !t.isOverdue && t.daysUntilDue <= 7).length;

  return {
    tasks,
    tasksByDate,
    overdueCount,
    dueSoonCount,
    isLoading,
    error: schedulesError ?? null,
    refetch: refetchSchedules,
  };
}
