/**
 * useMaintenanceAlerts Hook
 * 
 * Provides maintenance alerts data with React Query caching and SignalR real-time updates.
 * Features:
 * - Fetches all alerts from API
 * - Automatic cache invalidation on SignalR events
 * - Filters for active alerts (excludes dismissed/resolved)
 * - Severity-based sorting for priority display
 */

import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { maintenanceService } from '@/services/maintenanceService';
import { maintenanceSignalRService } from '@/services/maintenance-signalr';
import type { MaintenanceAlert } from '@/types/maintenance';
import { MaintenanceAlertStatus } from '@/types/maintenance';

export interface UseMaintenanceAlertsOptions {
  /** Filter to only show active alerts (default: true) */
  activeOnly?: boolean;
  /** Specific printer ID to filter alerts (optional) */
  printerId?: string;
  /** Polling interval in ms (default: 60000) */
  refetchInterval?: number;
}

export interface UseMaintenanceAlertsResult {
  /** All fetched alerts (filtered based on options) */
  alerts: MaintenanceAlert[];
  /** Whether the query is currently loading */
  isLoading: boolean;
  /** Error from the query, if any */
  error: Error | null;
  /** Refetch alerts manually */
  refetch: () => void;
  /** Count of alerts by severity */
  severityCounts: {
    critical: number;
    high: number;
    medium: number;
    low: number;
  };
  /** Total active alert count */
  totalActive: number;
}

const QUERY_KEY = 'maintenance-alerts';

/**
 * Hook for fetching and managing maintenance alerts
 */
export function useMaintenanceAlerts(
  options: UseMaintenanceAlertsOptions = {}
): UseMaintenanceAlertsResult {
  const { activeOnly = true, printerId, refetchInterval = 60000 } = options;
  const queryClient = useQueryClient();

  // Fetch alerts from API
  const { data, isLoading, error, refetch } = useQuery<MaintenanceAlert[], Error>({
    queryKey: printerId ? [QUERY_KEY, printerId] : [QUERY_KEY],
    queryFn: async () => {
      if (printerId) {
        return maintenanceService.getPrinterAlerts(printerId);
      }
      return maintenanceService.getAllAlerts();
    },
    refetchInterval,
    staleTime: 30000, // 30 seconds
  });

  // Subscribe to SignalR events for real-time updates
  useEffect(() => {
    let unsubCreate: (() => void) | undefined;
    let unsubStatus: (() => void) | undefined;

    const setupSubscriptions = async () => {
      try {
        await maintenanceSignalRService.start();

        // Invalidate cache when new alert created
        unsubCreate = maintenanceSignalRService.onAlertCreated(() => {
          queryClient.invalidateQueries({ queryKey: [QUERY_KEY] });
        });

        // Invalidate cache when alert status changes
        unsubStatus = maintenanceSignalRService.onAlertStatusChanged(() => {
          queryClient.invalidateQueries({ queryKey: [QUERY_KEY] });
        });
      } catch (err) {
        console.warn('[useMaintenanceAlerts] Failed to connect to SignalR:', err);
      }
    };

    setupSubscriptions();

    return () => {
      unsubCreate?.();
      unsubStatus?.();
    };
  }, [queryClient]);

  // Filter and process alerts
  const alerts = (data || []).filter((alert) => {
    if (activeOnly) {
      // Include Active and Acknowledged, exclude Resolved and Dismissed
      return (
        alert.status === MaintenanceAlertStatus.Active ||
        alert.status === MaintenanceAlertStatus.Acknowledged
      );
    }
    return true;
  });

  // Sort by severity (critical first) then by creation date (newest first)
  const sortedAlerts = [...alerts].sort((a, b) => {
    if (a.severity !== b.severity) {
      return b.severity - a.severity; // Higher severity first
    }
    return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
  });

  // Calculate severity counts
  const severityCounts = sortedAlerts.reduce(
    (acc, alert) => {
      switch (alert.severity) {
        case 4:
          acc.critical++;
          break;
        case 3:
          acc.high++;
          break;
        case 2:
          acc.medium++;
          break;
        case 1:
          acc.low++;
          break;
      }
      return acc;
    },
    { critical: 0, high: 0, medium: 0, low: 0 }
  );

  return {
    alerts: sortedAlerts,
    isLoading,
    error: error ?? null,
    refetch,
    severityCounts,
    totalActive: sortedAlerts.length,
  };
}
