/**
 * useScheduleDeployments Hook
 *
 * Provides React Query hooks for schedule deployments (plans deployed to printers).
 * Fetches from /api/maintenance/schedules.
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { maintenancePlanService } from '@/services/maintenancePlanService';
import type {
  DeployMaintenancePlanDto,
  UpdateScheduleDeploymentDto,
} from '@/types/maintenance';

// ──────────────────────── Query Keys ────────────────────────
export const scheduleKeys = {
  all: ['scheduleDeployments'] as const,
  lists: () => [...scheduleKeys.all, 'list'] as const,
  list: (printerId?: string, planId?: string, activeOnly?: boolean) =>
    [...scheduleKeys.lists(), { printerId, planId, activeOnly }] as const,
  details: () => [...scheduleKeys.all, 'detail'] as const,
  detail: (id: string) => [...scheduleKeys.details(), id] as const,
};

// ──────────────────────── Queries ────────────────────────

export function useScheduleDeployments(printerId?: string, planId?: string, activeOnly?: boolean) {
  return useQuery({
    queryKey: scheduleKeys.list(printerId, planId, activeOnly),
    queryFn: () => maintenancePlanService.getScheduleDeployments(printerId, planId, activeOnly),
  });
}

export function useScheduleDeployment(id: string | undefined) {
  return useQuery({
    queryKey: scheduleKeys.detail(id!),
    queryFn: () => maintenancePlanService.getScheduleDeploymentById(id!),
    enabled: !!id,
  });
}

// ──────────────────────── Mutations ────────────────────────

export function useDeployPlan() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (data: DeployMaintenancePlanDto) =>
      maintenancePlanService.deployPlan(data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: scheduleKeys.all });
    },
  });
}

export function useUpdateScheduleDeployment() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, data }: { id: string; data: UpdateScheduleDeploymentDto }) =>
      maintenancePlanService.updateScheduleDeployment(id, data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: scheduleKeys.all });
    },
  });
}

export function useDeleteScheduleDeployment() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      maintenancePlanService.deleteScheduleDeployment(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: scheduleKeys.all });
    },
  });
}
