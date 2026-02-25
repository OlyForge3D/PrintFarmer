/**
 * useMaintenancePlans Hook
 *
 * Provides hierarchical maintenance plan data with React Query caching.
 * Fetches from /api/maintenance/plans (new hierarchical API).
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { maintenancePlanService } from '@/services/maintenancePlanService';
import type {
  CreateMaintenancePlanDto,
  UpdateMaintenancePlanDto,
  CreateMaintenanceTaskDto,
  UpdateMaintenanceTaskDto,
  AddTaskComponentDto,
} from '@/types/maintenance';

// ──────────────────────── Query Keys ────────────────────────
export const planKeys = {
  all: ['maintenancePlans'] as const,
  lists: () => [...planKeys.all, 'list'] as const,
  list: (activeOnly?: boolean) => [...planKeys.lists(), { activeOnly }] as const,
  details: () => [...planKeys.all, 'detail'] as const,
  detail: (id: string) => [...planKeys.details(), id] as const,
  forPrinter: (printerId: string) => [...planKeys.all, 'forPrinter', printerId] as const,
};

// ──────────────────────── Plans ────────────────────────

export function useMaintenancePlans(activeOnly?: boolean) {
  return useQuery({
    queryKey: planKeys.list(activeOnly),
    queryFn: () => maintenancePlanService.getPlans(activeOnly),
  });
}

export function useMaintenancePlan(id: string | undefined) {
  return useQuery({
    queryKey: planKeys.detail(id!),
    queryFn: () => maintenancePlanService.getPlanById(id!),
    enabled: !!id,
  });
}

export function usePlansForPrinter(printerId: string | undefined) {
  return useQuery({
    queryKey: planKeys.forPrinter(printerId!),
    queryFn: () => maintenancePlanService.getPlansForPrinter(printerId!),
    enabled: !!printerId,
  });
}

export function useCreatePlan() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (data: CreateMaintenancePlanDto) => maintenancePlanService.createPlan(data),
    onSuccess: () => qc.invalidateQueries({ queryKey: planKeys.all }),
  });
}

export function useUpdatePlan() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, data }: { id: string; data: UpdateMaintenancePlanDto }) =>
      maintenancePlanService.updatePlan(id, data),
    onSuccess: () => qc.invalidateQueries({ queryKey: planKeys.all }),
  });
}

export function useDeletePlan() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => maintenancePlanService.deletePlan(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: planKeys.all }),
  });
}

// ──────────────────────── Tasks ────────────────────────

export function useCreateTask(planId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (data: CreateMaintenanceTaskDto) => maintenancePlanService.createTask(planId, data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: planKeys.detail(planId) });
      qc.invalidateQueries({ queryKey: planKeys.lists() });
    },
  });
}

export function useUpdateTask(planId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ taskId, data }: { taskId: string; data: UpdateMaintenanceTaskDto }) =>
      maintenancePlanService.updateTask(planId, taskId, data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: planKeys.detail(planId) });
      qc.invalidateQueries({ queryKey: planKeys.lists() });
    },
  });
}

export function useDeleteTask(planId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (taskId: string) => maintenancePlanService.deleteTask(planId, taskId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: planKeys.detail(planId) });
      qc.invalidateQueries({ queryKey: planKeys.lists() });
    },
  });
}

// ──────────────────── Task Components ──────────────────

export function useAddTaskComponent(planId: string, taskId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (data: AddTaskComponentDto) =>
      maintenancePlanService.addTaskComponent(planId, taskId, data),
    onSuccess: () => qc.invalidateQueries({ queryKey: planKeys.detail(planId) }),
  });
}

export function useRemoveTaskComponent(planId: string, taskId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (componentId: string) =>
      maintenancePlanService.removeTaskComponent(planId, taskId, componentId),
    onSuccess: () => qc.invalidateQueries({ queryKey: planKeys.detail(planId) }),
  });
}
