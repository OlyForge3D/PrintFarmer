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
import { maintenanceQueryKeys } from '../queryKeys';

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
      // Deploying a new schedule inserts a row the upcoming feed will
      // surface as soon as its computed due-date lands in the lookahead
      // window (or immediately if `includeOverdue` is set). Invalidate
      // by prefix — react-query matches every variant of the feed key
      // (`[key, { lookaheadDays, includeOverdue, printerId }]`) with a
      // partial prefix match, so this reaches all filters at once.
      qc.invalidateQueries({ queryKey: maintenanceQueryKeys.upcomingMaintenance() });
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
      // Updating a deployment (interval, active toggle, notes) changes
      // the schedule engine's next-due computation — the upcoming feed
      // must be re-derived.
      qc.invalidateQueries({ queryKey: maintenanceQueryKeys.upcomingMaintenance() });
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
      // Undeploying (delete) removes a source row for the upcoming feed;
      // any due-soon entries backed by this deployment must drop out of
      // the operator's view immediately, not on the next 2-minute poll.
      qc.invalidateQueries({ queryKey: maintenanceQueryKeys.upcomingMaintenance() });
    },
  });
}
