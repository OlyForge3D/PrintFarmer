/**
 * useComponentMaintenance Hook
 * 
 * Provides component-specific maintenance data aggregated across the fleet.
 * Groups maintenance schedules and logs by component type for tracking
 * and analysis purposes.
 */

import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { maintenanceService } from '@/services/maintenanceService';
import { maintenancePlanService } from '@/services/maintenancePlanService';
import type { MaintenanceLog } from '@/types/maintenance';
import type { MaintenanceTaskDto } from '@/types/maintenance';
import { parseISO } from 'date-fns';

/**
 * Represents maintenance data for a specific component type
 */
export interface ComponentMaintenanceData {
  /** Component name (e.g., "Hotend", "Bed", "Belts") */
  component: string;
  /** Number of schedules for this component */
  scheduleCount: number;
  /** Total maintenance events logged for this component */
  maintenanceCount: number;
  /** Average days between maintenance events */
  averageIntervalDays: number | null;
  /** Total cost spent on this component */
  totalCost: number;
  /** Number of printers with this component scheduled */
  printerCount: number;
  /** Most recent maintenance date */
  lastMaintenanceDate: Date | null;
  /** Recent maintenance logs for this component */
  recentLogs: MaintenanceLog[];
  /** Active tasks for this component */
  tasks: MaintenanceTaskDto[];
}

/**
 * Represents a replacement event for tracking component replacements
 */
export interface ComponentReplacement {
  id: string;
  component: string;
  printerId: string;
  printerName: string;
  replacedAt: Date;
  partsReplaced: string;
  cost: number | null;
  performedBy: string | null;
  notes: string | null;
}

export interface UseComponentMaintenanceOptions {
  /** Filter by specific component name */
  component?: string;
  /** Filter by printer ID */
  printerId?: string;
  /** Enable/disable query */
  enabled?: boolean;
}

export interface UseComponentMaintenanceResult {
  /** Component maintenance data grouped by component type */
  componentData: ComponentMaintenanceData[];
  /** Component replacements (logs with partsReplaced) */
  replacements: ComponentReplacement[];
  /** All unique component names */
  componentNames: string[];
  /** Total cost across all components */
  totalMaintenanceCost: number;
  /** Loading state */
  isLoading: boolean;
  /** Error state */
  error: Error | null;
  /** Refetch function */
  refetch: () => void;
}

/**
 * Standard component categories for grouping
 */
export const COMPONENT_CATEGORIES = [
  'Hotend',
  'Nozzle',
  'Bed',
  'Belts',
  'Bearings',
  'Fans',
  'Extruder',
  'Motors',
  'Electronics',
  'Frame',
  'Other'
] as const;

/**
 * Normalize component name to standard category
 */
function normalizeComponent(component: string | null | undefined): string {
  if (!component) return 'Other';
  
  const lower = component.toLowerCase();
  
  if (lower.includes('hotend') || lower.includes('hot end')) return 'Hotend';
  if (lower.includes('nozzle')) return 'Nozzle';
  if (lower.includes('bed') || lower.includes('plate')) return 'Bed';
  if (lower.includes('belt')) return 'Belts';
  if (lower.includes('bearing') || lower.includes('bushing')) return 'Bearings';
  if (lower.includes('fan') || lower.includes('cooling')) return 'Fans';
  if (lower.includes('extrud')) return 'Extruder';
  if (lower.includes('motor') || lower.includes('stepper')) return 'Motors';
  if (lower.includes('board') || lower.includes('electronic') || lower.includes('wir')) return 'Electronics';
  if (lower.includes('frame') || lower.includes('chassis')) return 'Frame';
  
  return component; // Return original if no match
}

/**
 * Hook for component-specific maintenance tracking
 */
export function useComponentMaintenance(
  options: UseComponentMaintenanceOptions = {}
): UseComponentMaintenanceResult {
  const { component, enabled = true } = options;

  // Fetch all tasks from the V3 task catalog
  const tasksQuery = useQuery({
    queryKey: ['taskCatalog', 'list', { activeOnly: true }],
    queryFn: () => maintenancePlanService.getCatalogTasks(undefined, true),
    enabled,
    staleTime: 30000,
  });

  // Fetch all alerts to get printer associations
  const alertsQuery = useQuery({
    queryKey: ['maintenance', 'alerts', 'all'],
    queryFn: () => maintenanceService.getAllAlerts(),
    enabled,
    staleTime: 30000,
  });

  // Process data into component-centric view
  const result = useMemo(() => {
    const tasks = tasksQuery.data || [];
    // alerts data is used to trigger re-computation when alerts change
    const _alerts = alertsQuery.data || [];
    void _alerts; // Suppress unused variable warning - used for dependency tracking

    // Group tasks by category (equivalent to old component grouping)
    const componentMap = new Map<string, {
      tasks: MaintenanceTaskDto[];
      logs: MaintenanceLog[];
      printerIds: Set<string>;
    }>();

    // Process tasks
    tasks.forEach(task => {
      const comp = normalizeComponent(task.category);
      
      // Apply component filter
      if (component && comp !== component) return;

      if (!componentMap.has(comp)) {
        componentMap.set(comp, { tasks: [], logs: [], printerIds: new Set() });
      }
      
      const data = componentMap.get(comp)!;
      data.tasks.push(task);
    });

    // Build component data array
    const componentData: ComponentMaintenanceData[] = [];
    const allReplacements: ComponentReplacement[] = [];
    let totalCost = 0;

    componentMap.forEach((data, comp) => {
      // Calculate average interval from tasks
      const intervalsInDays = data.tasks
        .map(t => t.intervalDays || (t.intervalHours ? t.intervalHours / 24 : null))
        .filter((d): d is number => d !== null);
      
      const avgInterval = intervalsInDays.length > 0
        ? intervalsInDays.reduce((a, b) => a + b, 0) / intervalsInDays.length
        : null;

      const componentCost = data.logs.reduce((sum, log) => sum + (log.cost || 0), 0);
      totalCost += componentCost;

      // Find last maintenance date
      const lastLog = data.logs.length > 0
        ? data.logs.sort((a, b) => 
            new Date(b.performedAt).getTime() - new Date(a.performedAt).getTime()
          )[0]
        : null;

      componentData.push({
        component: comp,
        scheduleCount: data.tasks.length,
        maintenanceCount: data.logs.length,
        averageIntervalDays: avgInterval,
        totalCost: componentCost,
        printerCount: data.printerIds.size,
        lastMaintenanceDate: lastLog ? parseISO(lastLog.performedAt) : null,
        recentLogs: data.logs.slice(0, 5),
        tasks: data.tasks,
      });

      // Extract replacements
      data.logs
        .filter(log => log.partsReplaced)
        .forEach(log => {
          allReplacements.push({
            id: log.id,
            component: comp,
            printerId: log.printerId,
            printerName: log.printer?.name || 'Unknown',
            replacedAt: parseISO(log.performedAt),
            partsReplaced: log.partsReplaced!,
            cost: log.cost ?? null,
            performedBy: log.performedBy ?? null,
            notes: log.notes ?? null,
          });
        });
    });

    // Sort by maintenance count descending
    componentData.sort((a, b) => b.scheduleCount - a.scheduleCount);

    // Get unique component names
    const componentNames = Array.from(componentMap.keys()).sort();

    // Sort replacements by date
    allReplacements.sort((a, b) => b.replacedAt.getTime() - a.replacedAt.getTime());

    return {
      componentData,
      replacements: allReplacements,
      componentNames,
      totalMaintenanceCost: totalCost,
    };
  }, [tasksQuery.data, alertsQuery.data, component]);

  return {
    ...result,
    isLoading: tasksQuery.isLoading || alertsQuery.isLoading,
    error: tasksQuery.error || alertsQuery.error,
    refetch: () => {
      tasksQuery.refetch();
      alertsQuery.refetch();
    },
  };
}
