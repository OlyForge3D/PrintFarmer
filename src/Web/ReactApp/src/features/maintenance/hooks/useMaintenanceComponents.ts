/**
 * useMaintenanceComponents Hook
 *
 * Provides parts inventory data with React Query caching.
 * Fetches from /api/maintenance/components (new hierarchical API).
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { maintenancePlanService } from '@/services/maintenancePlanService';
import type {
  CreateMaintenanceComponentDto,
  UpdateMaintenanceComponentDto,
  MaintenanceExportEnvelope,
} from '@/types/maintenance';

// ──────────────────────── Query Keys ────────────────────────
export const componentKeys = {
  all: ['maintenanceComponents'] as const,
  lists: () => [...componentKeys.all, 'list'] as const,
  list: (category?: string) => [...componentKeys.lists(), { category }] as const,
  detail: (id: string) => [...componentKeys.all, 'detail', id] as const,
  categories: () => [...componentKeys.all, 'categories'] as const,
  lowStock: () => [...componentKeys.all, 'low-stock'] as const,
};

export function useMaintenanceComponents(category?: string) {
  return useQuery({
    queryKey: componentKeys.list(category),
    queryFn: () => maintenancePlanService.getComponents(category),
  });
}

export function useComponentCategories() {
  return useQuery({
    queryKey: componentKeys.categories(),
    queryFn: () => maintenancePlanService.getCategories(),
  });
}

export function useLowStockComponents() {
  return useQuery({
    queryKey: componentKeys.lowStock(),
    queryFn: () => maintenancePlanService.getLowStock(),
  });
}

export function useCreateComponent() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (data: CreateMaintenanceComponentDto) => maintenancePlanService.createComponent(data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: componentKeys.all });
    },
  });
}

export function useUpdateComponent() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, data }: { id: string; data: UpdateMaintenanceComponentDto }) =>
      maintenancePlanService.updateComponent(id, data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: componentKeys.all });
    },
  });
}

export function useDeleteComponent() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => maintenancePlanService.deleteComponent(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: componentKeys.all });
    },
  });
}

export function useExportComponents() {
  return useMutation({
    mutationFn: () => maintenancePlanService.exportComponents(),
  });
}

export function useImportComponents() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (envelope: MaintenanceExportEnvelope) =>
      maintenancePlanService.importComponents(envelope),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: componentKeys.all });
    },
  });
}
