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
import type { MaintenanceSchedule, MaintenanceLog } from '@/types/maintenance';
import { differenceInDays, parseISO } from 'date-fns';

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
  /** Active schedules for this component */
  schedules: MaintenanceSchedule[];
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
  const { component, printerId, enabled = true } = options;

  // Fetch all schedules
  const schedulesQuery = useQuery({
    queryKey: ['maintenance', 'schedules', 'all'],
    queryFn: () => maintenanceService.getAllSchedules(),
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
    const schedules = schedulesQuery.data || [];
    const alerts = alertsQuery.data || [];

    // Group schedules by component
    const componentMap = new Map<string, {
      schedules: MaintenanceSchedule[];
      logs: MaintenanceLog[];
      printerIds: Set<string>;
    }>();

    // Process schedules
    schedules.forEach(schedule => {
      const comp = normalizeComponent(schedule.component);
      
      // Apply filters
      if (component && comp !== component) return;
      if (printerId && schedule.printerId !== printerId) return;

      if (!componentMap.has(comp)) {
        componentMap.set(comp, { schedules: [], logs: [], printerIds: new Set() });
      }
      
      const data = componentMap.get(comp)!;
      data.schedules.push(schedule);
      if (schedule.printerId) {
        data.printerIds.add(schedule.printerId);
      }
    });

    // Build component data array
    const componentData: ComponentMaintenanceData[] = [];
    const allReplacements: ComponentReplacement[] = [];
    let totalCost = 0;

    componentMap.forEach((data, comp) => {
      // Calculate average interval from schedules
      const intervalsInDays = data.schedules
        .map(s => s.intervalDays || (s.intervalHours ? s.intervalHours / 24 : null))
        .filter((d): d is number => d !== null);
      
      const avgInterval = intervalsInDays.length > 0
        ? intervalsInDays.reduce((a, b) => a + b, 0) / intervalsInDays.length
        : null;

      // Calculate cost from logs (we'd need to fetch logs per printer for accurate cost)
      // For now, estimate based on schedules
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
        scheduleCount: data.schedules.length,
        maintenanceCount: data.logs.length,
        averageIntervalDays: avgInterval,
        totalCost: componentCost,
        printerCount: data.printerIds.size,
        lastMaintenanceDate: lastLog ? parseISO(lastLog.performedAt) : null,
        recentLogs: data.logs.slice(0, 5),
        schedules: data.schedules,
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
  }, [schedulesQuery.data, alertsQuery.data, component, printerId]);

  return {
    ...result,
    isLoading: schedulesQuery.isLoading || alertsQuery.isLoading,
    error: schedulesQuery.error || alertsQuery.error,
    refetch: () => {
      schedulesQuery.refetch();
      alertsQuery.refetch();
    },
  };
}
