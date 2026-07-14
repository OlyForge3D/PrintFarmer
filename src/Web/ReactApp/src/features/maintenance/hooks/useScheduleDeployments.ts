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

/**
 * Query-key prefix for the fleet-wide upcoming-maintenance feed
 * (`useUpcomingMaintenance` in `../hooks/useUpcomingMaintenance.ts`).
 * Owned by that module — duplicated as a `readonly` literal here so
 * schedule-deployment mutations can cross-invalidate the feed without
 * pulling the hook (and its query function) into this file's dependency
 * graph. Kept in a single named constant so future readers can grep for
 * every consumer of the prefix.
 *
 * Any create/update/delete on a schedule deployment mutates the upstream
 * data that the upcoming feed derives from (per-printer schedule
 * intervals + last-performed watermarks). Failing to invalidate the feed
 * leaves the operator's "next due" list stale until the polling interval
 * (default 120s) elapses.
 */
export const UPCOMING_MAINTENANCE_KEY_PREFIX = ['upcoming-maintenance'] as const;

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
      qc.invalidateQueries({ queryKey: UPCOMING_MAINTENANCE_KEY_PREFIX });
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
      // must be re-derived. See UPCOMING_MAINTENANCE_KEY_PREFIX above.
      qc.invalidateQueries({ queryKey: UPCOMING_MAINTENANCE_KEY_PREFIX });
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
      qc.invalidateQueries({ queryKey: UPCOMING_MAINTENANCE_KEY_PREFIX });
    },
  });
}
