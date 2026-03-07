import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo } from 'react';
import { apiClient } from '@/services/api';
import { locationService, type LocationTreeNode } from '@/services/locationService';
import type { Printer } from '@/types/api';
import { printerSignalRService } from '@/services/printer-signalr';

export const locationDashboardKeys = {
  tree: ['locations', 'tree'] as const,
  printers: (locationId: string) => ['locations', locationId, 'printers'] as const,
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

export function computeStats(printers: Printer[]): LocationStats {
  const online = printers.filter(p => p.isOnline);
  const printing = online.filter(p => p.state === 'Printing');
  const idle = online.filter(p => p.state === 'Idle' || p.state === 'Ready' || p.state === 'Operational');

  return {
    totalPrinters: printers.length,
    online: online.length,
    offline: printers.length - online.length,
    printing: printing.length,
    idle: idle.length,
    activeJobs: printing.length,
  };
}

export function collectLocationIds(node: LocationTreeNode): string[] {
  const ids = [node.id];
  for (const child of node.children) {
    ids.push(...collectLocationIds(child));
  }
  return ids;
}

export function findNode(nodes: LocationTreeNode[], id: string): LocationTreeNode | undefined {
  for (const node of nodes) {
    if (node.id === id) return node;
    const found = findNode(node.children, id);
    if (found) return found;
  }
  return undefined;
}

export function useLocationTree() {
  return useQuery({
    queryKey: locationDashboardKeys.tree,
    queryFn: () => locationService.getLocationTree(),
    staleTime: 60_000,
  });
}

export function useLocationPrinters(locationId: string | null) {
  const { data: allPrinters = [], isLoading, error } = useQuery({
    queryKey: ['printers'],
    queryFn: () => apiClient.getPrinters() as Promise<Printer[]>,
    staleTime: 30_000,
  });

  const { data: tree = [] } = useLocationTree();

  const filteredPrinters = useMemo(() => {
    if (!locationId) return allPrinters;
    const node = findNode(tree, locationId);
    if (!node) return [];
    const descendantIds = new Set(collectLocationIds(node));
    return allPrinters.filter(p => p.location && descendantIds.has(p.location.id));
  }, [allPrinters, tree, locationId]);

  return { data: filteredPrinters, isLoading, error };
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
      queryClient.invalidateQueries({ queryKey: ['printers'] });
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
