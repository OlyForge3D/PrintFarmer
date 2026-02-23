/**
 * useMaintenanceStats Hook
 * 
 * Provides aggregated maintenance statistics for the fleet.
 * Combines alert data with printer statistics for comprehensive overview.
 */

import { useQuery } from '@tanstack/react-query';
import { usePrinters } from '@/common/hooks/useApi';
import { maintenanceService } from '@/services/maintenanceService';
import type { MaintenanceAlert } from '@/types/maintenance';
import { MaintenanceAlertStatus } from '@/types/maintenance';

export interface PrinterMaintenanceStatus {
  printerId: string;
  printerName: string;
  isOnline: boolean;
  inMaintenance: boolean;
  activeAlertCount: number;
  criticalAlerts: number;
  highAlerts: number;
  mediumAlerts: number;
  lowAlerts: number;
  lastMaintenanceDate?: string;
  totalPrintHours?: number;
}

export interface FleetMaintenanceStats {
  /** Total printers in fleet */
  totalPrinters: number;
  /** Printers currently online */
  printersOnline: number;
  /** Printers currently in maintenance mode */
  printersInMaintenance: number;
  /** Printers with active alerts requiring attention */
  printersNeedingAttention: number;
  /** Alert breakdown by severity */
  alertsBySeveity: {
    critical: number;
    high: number;
    medium: number;
    low: number;
  };
  /** Total active alerts across all printers */
  totalActiveAlerts: number;
  /** Printers with status breakdown */
  printerStatuses: PrinterMaintenanceStatus[];
}

export interface UseMaintenanceStatsOptions {
  /** Polling interval in ms (default: 60000) */
  refetchInterval?: number;
}

export interface UseMaintenanceStatsResult {
  /** Aggregated fleet statistics */
  stats: FleetMaintenanceStats | null;
  /** Whether data is loading */
  isLoading: boolean;
  /** Error from queries */
  error: Error | null;
  /** Refetch all data */
  refetch: () => void;
}

const QUERY_KEY = 'maintenance-stats';

/**
 * Hook for fetching aggregated maintenance statistics
 */
export function useMaintenanceStats(
  options: UseMaintenanceStatsOptions = {}
): UseMaintenanceStatsResult {
  const { refetchInterval = 60000 } = options;

  // Fetch printers for base data
  const { data: printers, isLoading: printersLoading, error: printersError } = usePrinters();

  // Fetch all alerts
  const { 
    data: alerts, 
    isLoading: alertsLoading, 
    error: alertsError,
    refetch: refetchAlerts 
  } = useQuery<MaintenanceAlert[], Error>({
    queryKey: [QUERY_KEY, 'alerts'],
    queryFn: () => maintenanceService.getAllAlerts(),
    refetchInterval,
    staleTime: 30000,
  });

  const isLoading = printersLoading || alertsLoading;
  const error = printersError || alertsError || null;

  // Calculate aggregated stats
  const stats: FleetMaintenanceStats | null = !isLoading && printers && alerts ? (() => {
    // Filter active alerts
    const activeAlerts = alerts.filter(
      a => a.status === MaintenanceAlertStatus.Active || 
           a.status === MaintenanceAlertStatus.Acknowledged
    );

    // Group alerts by printer
    const alertsByPrinter = new Map<string, MaintenanceAlert[]>();
    activeAlerts.forEach(alert => {
      const existing = alertsByPrinter.get(alert.printerId) || [];
      alertsByPrinter.set(alert.printerId, [...existing, alert]);
    });

    // Calculate severity counts
    const alertsBySeverity = activeAlerts.reduce(
      (acc, alert) => {
        switch (alert.severity) {
          case 4: acc.critical++; break;
          case 3: acc.high++; break;
          case 2: acc.medium++; break;
          case 1: acc.low++; break;
        }
        return acc;
      },
      { critical: 0, high: 0, medium: 0, low: 0 }
    );

    // Build printer status list
    const printerStatuses: PrinterMaintenanceStatus[] = printers.map(printer => {
      const printerAlerts = alertsByPrinter.get(printer.id) || [];
      return {
        printerId: printer.id,
        printerName: printer.name || 'Unknown Printer',
        isOnline: printer.isOnline ?? false,
        inMaintenance: printer.inMaintenance ?? false,
        activeAlertCount: printerAlerts.length,
        criticalAlerts: printerAlerts.filter(a => a.severity === 4).length,
        highAlerts: printerAlerts.filter(a => a.severity === 3).length,
        mediumAlerts: printerAlerts.filter(a => a.severity === 2).length,
        lowAlerts: printerAlerts.filter(a => a.severity === 1).length,
        totalPrintHours: undefined, // Will be populated when statistics endpoint is available
      };
    });

    // Sort by alert severity/count
    printerStatuses.sort((a, b) => {
      // First by critical alerts
      if (a.criticalAlerts !== b.criticalAlerts) return b.criticalAlerts - a.criticalAlerts;
      // Then by total alerts
      return b.activeAlertCount - a.activeAlertCount;
    });

    return {
      totalPrinters: printers.length,
      printersOnline: printers.filter(p => p.isOnline).length,
      printersInMaintenance: printers.filter(p => p.inMaintenance).length,
      printersNeedingAttention: alertsByPrinter.size,
      alertsBySeveity: alertsBySeverity,
      totalActiveAlerts: activeAlerts.length,
      printerStatuses,
    };
  })() : null;

  const refetch = () => {
    refetchAlerts();
  };

  return {
    stats,
    isLoading,
    error: error as Error | null,
    refetch,
  };
}
