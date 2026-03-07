import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect } from 'react';
import { apiClient } from '@/services/api';
import { locationService, findNode } from '@/services/locationService';
import type { LocationTreeNode, LocationSubtreePrinter } from '@/types/api';
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
  subtreePrinters: (locationId: string) => ['locations', locationId, 'subtree-printers'] as const,
  stats: (locationId: string) => ['locations', locationId, 'stats'] as const,
} as const;

export interface LocationStats {
  totalPrinters: number;
  online: number;
  offline: number;
  printing: number;
  idle: number;
  activeJobs: number;
}

export function computeStats(printers: LocationSubtreePrinter[]): LocationStats {
  const online = printers.filter(p => p.isOnline);
  const printing = online.filter(p => p.currentState === 'Printing');
  const idle = online.filter(p => 
    p.currentState === 'Idle' || 
    p.currentState === 'Ready' || 
    p.currentState === 'Operational'
  );

  return {
    totalPrinters: printers.length,
    online: online.length,
    offline: printers.length - online.length,
    printing: printing.length,
    idle: idle.length,
    activeJobs: printing.length,
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
    queryKey: locationId ? locationDashboardKeys.subtreePrinters(locationId) : ['locations', 'all-printers'],
    queryFn: async () => {
      if (!locationId) {
        // When no location selected, get all printers by fetching subtree for root locations
        const tree = await locationService.getLocationTree();
        const allPrinters: LocationSubtreePrinter[] = [];
        for (const root of tree) {
          const printers = await apiClient.getLocationSubtreePrinters(root.id);
          allPrinters.push(...printers);
        }
        return allPrinters;
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
