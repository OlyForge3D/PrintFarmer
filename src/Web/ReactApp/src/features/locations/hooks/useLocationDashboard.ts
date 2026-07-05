import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect } from 'react';
import { queryKeys } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import { locationService, findNode } from '@/services/locationService';
import type { LocationTreeNode, LocationSubtreePrinter, Printer } from '@/types/api';
import { printerSignalRService } from '@/services/printer-signalr';

export { findNode };
export type { LocationTreeNode };

export function collectLocationIds(node: LocationTreeNode): string[] {
  const ids = [node.id];
  for (const child of node.children) {
    ids.push(...collectLocationIds(child));
  }
  return ids;
}

export const locationDashboardKeys = {
  tree: ['locations', 'tree'] as const,
  allPrinters: ['locations', 'all-printers'] as const,
  subtreePrinters: (locationId: string) => ['locations', locationId, 'subtree-printers'] as const,
  stats: (locationId: string) => ['locations', locationId, 'stats'] as const,
} as const;

export interface LocationStats {
  totalPrinters: number;
  online: number;
  offline: number;
  attention: number;
  printing: number;
  idle: number;
  activeJobs: number;
}

const PRINTING_STATUSES = new Set(['Printing']);
const IDLE_STATUSES = new Set(['Idle']);
const ATTENTION_STATUSES = new Set(['Paused', 'Error', 'Offline', 'Shutdown', 'Halted', 'Disconnected', 'Cancelled']);

function hasStatus(printer: LocationSubtreePrinter, statuses: ReadonlySet<string>): boolean {
  return statuses.has(printer.status);
}

export function isActiveJob(printer: LocationSubtreePrinter): boolean {
  return hasStatus(printer, PRINTING_STATUSES) || Boolean(printer.currentJobName?.trim());
}

function toLocationSubtreePrinter(printer: Printer): LocationSubtreePrinter {
  return {
    printerId: printer.id,
    printerName: printer.name,
    locationId: printer.location?.id ?? null,
    locationName: printer.location?.name ?? null,
    isOnline: printer.isOnline,
    status: printer.state ?? (printer.isOnline ? 'Idle' : 'Offline'),
    currentJobName: printer.jobName ?? null,
  };
}

export function computeStats(printers: LocationSubtreePrinter[]): LocationStats {
  const online = printers.filter(p => p.isOnline);
  const printing = online.filter(p => hasStatus(p, PRINTING_STATUSES));
  const idle = online.filter(p => hasStatus(p, IDLE_STATUSES));
  const attention = printers.filter((printer) => (
    !printer.isOnline || hasStatus(printer, ATTENTION_STATUSES)
  ));

  return {
    totalPrinters: printers.length,
    online: online.length,
    offline: printers.length - online.length,
    attention: attention.length,
    printing: printing.length,
    idle: idle.length,
    activeJobs: printers.filter(isActiveJob).length,
  };
}

export function useLocationTree() {
  return useQuery({
    queryKey: locationDashboardKeys.tree,
    queryFn: () => locationService.getLocationTree(),
    staleTime: 60_000,
  });
}

export function useLocationPrinters(locationId: string | null) {
  return useQuery({
    queryKey: locationId ? locationDashboardKeys.subtreePrinters(locationId) : locationDashboardKeys.allPrinters,
    queryFn: async () => {
      if (!locationId) {
        const printers = await apiClient.getPrinters();
        return printers.map(toLocationSubtreePrinter);
      }
      return apiClient.getLocationSubtreePrinters(locationId);
    },
    staleTime: 10_000, // Real-time-ish data
    enabled: true,
  });
}

export function useLocationStats(locationId: string | null) {
  const { data: printers = [], isLoading, error } = useLocationPrinters(locationId);
  const stats = computeStats(printers);
  return { stats, isLoading, error };
}

export function useSignalRPrinterUpdates() {
  const queryClient = useQueryClient();

  const handleUpdate = useCallback(
    () => {
      // Invalidate all subtree-printers queries when printer status updates
      queryClient.invalidateQueries({ 
        predicate: (query) => 
          Array.isArray(query.queryKey) && 
          query.queryKey[0] === 'locations' &&
          (query.queryKey.includes('subtree-printers') || query.queryKey.includes('all-printers'))
      });
    },
    [queryClient],
  );

  useEffect(() => {
    const unsubscribe = printerSignalRService.onPrinterStatusUpdate(handleUpdate);

    return () => {
      unsubscribe();
    };
  }, [handleUpdate]);
}

export function invalidateLocationDashboardQueries(queryClient: ReturnType<typeof useQueryClient>) {
  queryClient.invalidateQueries({ queryKey: locationDashboardKeys.tree });
  queryClient.invalidateQueries({ queryKey: locationDashboardKeys.allPrinters });
  queryClient.invalidateQueries({ queryKey: queryKeys.printers });
  queryClient.invalidateQueries({
    predicate: (query) =>
      Array.isArray(query.queryKey) &&
      query.queryKey[0] === 'locations' &&
      query.queryKey.includes('subtree-printers'),
  });
}
