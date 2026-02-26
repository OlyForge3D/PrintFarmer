/**
 * useTaskCatalog Hook
 *
 * Provides React Query hooks for the standalone global task catalog.
 * Fetches from /api/maintenance/tasks (independent of plans).
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { maintenancePlanService } from '@/services/maintenancePlanService';
import type {
  CreateMaintenanceTaskDto,
  UpdateMaintenanceTaskDto,
  AddTaskComponentDto,
} from '@/types/maintenance';

// ──────────────────────── Query Keys ────────────────────────
export const taskCatalogKeys = {
  all: ['taskCatalog'] as const,
  lists: () => [...taskCatalogKeys.all, 'list'] as const,
  list: (category?: string, activeOnly?: boolean) =>
    [...taskCatalogKeys.lists(), { category, activeOnly }] as const,
  details: () => [...taskCatalogKeys.all, 'detail'] as const,
  detail: (id: string) => [...taskCatalogKeys.details(), id] as const,
  categories: () => [...taskCatalogKeys.all, 'categories'] as const,
};

// ──────────────────────── Queries ────────────────────────

export function useTaskCatalog(category?: string, activeOnly?: boolean) {
  return useQuery({
    queryKey: taskCatalogKeys.list(category, activeOnly),
    queryFn: () => maintenancePlanService.getCatalogTasks(category, activeOnly),
  });
}

export function useTaskCatalogItem(id: string | undefined) {
  return useQuery({
    queryKey: taskCatalogKeys.detail(id!),
    queryFn: () => maintenancePlanService.getCatalogTaskById(id!),
    enabled: !!id,
  });
}

export function useTaskCategories() {
  return useQuery({
    queryKey: taskCatalogKeys.categories(),
    queryFn: () => maintenancePlanService.getTaskCategories(),
  });
}

// ──────────────────────── Mutations ────────────────────────

export function useCreateCatalogTask() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (data: CreateMaintenanceTaskDto) =>
      maintenancePlanService.createCatalogTask(data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: taskCatalogKeys.all });
    },
  });
}

export function useUpdateCatalogTask() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, data }: { id: string; data: UpdateMaintenanceTaskDto }) =>
      maintenancePlanService.updateCatalogTask(id, data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: taskCatalogKeys.all });
    },
  });
}

export function useDeleteCatalogTask() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => maintenancePlanService.deleteCatalogTask(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: taskCatalogKeys.all });
    },
  });
}

export function useAddCatalogTaskComponent() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ taskId, data }: { taskId: string; data: AddTaskComponentDto }) =>
      maintenancePlanService.addCatalogTaskComponent(taskId, data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: taskCatalogKeys.all });
    },
  });
}

export function useRemoveCatalogTaskComponent() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ taskId, componentId }: { taskId: string; componentId: string }) =>
      maintenancePlanService.removeCatalogTaskComponent(taskId, componentId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: taskCatalogKeys.all });
    },
  });
}
