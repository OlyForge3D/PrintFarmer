/**
 * React Query hooks for filament fallback groups (issue #718).
 *
 * - `useFallbackGroups(printerId)` — list all groups for one printer.
 * - `useCreateFallbackGroup / useUpdateFallbackGroup / useDeleteFallbackGroup`
 *   — mutations that invalidate the list on success.
 * - SignalR `fallbackgroupsupdated` cues are attached exactly once per
 *   mounted `QueryClient`; they invalidate the printer-scoped list so the
 *   next render refetches the authoritative rows.
 */
import { useEffect } from "react";
import {
  useMutation,
  useQuery,
  useQueryClient,
  type QueryClient,
  type UseQueryResult,
} from "@tanstack/react-query";

import { printerSignalRService } from "@/services/printer-signalr";
import { fallbackGroupsService } from "./service";
import type {
  CreateFilamentFallbackGroupRequest,
  FallbackGroupsUpdatedEvent,
  FilamentFallbackGroup,
  UpdateFilamentFallbackGroupRequest,
} from "./types";

export const fallbackGroupsQueryKeys = {
  all: ["fallback-groups"] as const,
  byPrinter: (printerId: string) =>
    [...fallbackGroupsQueryKeys.all, "printer", printerId] as const,
};

const STALE_MS = 15_000;

/**
 * Reference-counted map of query clients listening for fallback group events.
 * Mirrors the pattern used for `filamentcoveragechanged` so we register one
 * SignalR handler per client and dispose it when the last consumer unmounts.
 */
const registeredClients = new Map<QueryClient, number>();
let signalRUnsubscribe: (() => void) | undefined;

function invalidateForEvent(event: FallbackGroupsUpdatedEvent) {
  registeredClients.forEach((_count, qc) => {
    if (event.printerId) {
      void qc.invalidateQueries({
        queryKey: fallbackGroupsQueryKeys.byPrinter(event.printerId),
      });
    } else {
      // Fleet-wide invalidation: drop every printer subscription.
      void qc.invalidateQueries({ queryKey: fallbackGroupsQueryKeys.all });
    }
  });
}

function ensureSignalRSubscription() {
  if (signalRUnsubscribe) return;
  void printerSignalRService.connect();
  signalRUnsubscribe = printerSignalRService.onFallbackGroupsUpdated(invalidateForEvent);
}

function useFallbackGroupsSignalRSync(enabled: boolean) {
  const queryClient = useQueryClient();
  useEffect(() => {
    if (!enabled) return;

    const prev = registeredClients.get(queryClient) ?? 0;
    registeredClients.set(queryClient, prev + 1);
    ensureSignalRSubscription();

    return () => {
      const remaining = (registeredClients.get(queryClient) ?? 1) - 1;
      if (remaining <= 0) {
        registeredClients.delete(queryClient);
      } else {
        registeredClients.set(queryClient, remaining);
      }
      if (registeredClients.size === 0 && signalRUnsubscribe) {
        signalRUnsubscribe();
        signalRUnsubscribe = undefined;
      }
    };
  }, [enabled, queryClient]);
}

/** Test-only reset for module-level SignalR wiring. */
export function __resetFallbackGroupsSubscriptionForTests(): void {
  if (signalRUnsubscribe) {
    signalRUnsubscribe();
    signalRUnsubscribe = undefined;
  }
  registeredClients.clear();
}

export interface UseFallbackGroupsOptions {
  enabled?: boolean;
}

/**
 * Fetch every fallback group configured for a single printer. Returns an
 * empty array on success when the printer has no groups. Errors surface via
 * the standard `error` field so the caller can render an inline alert.
 */
export function useFallbackGroups(
  printerId: string | null | undefined,
  options: UseFallbackGroupsOptions = {},
): UseQueryResult<FilamentFallbackGroup[]> {
  const enabled = (options.enabled ?? true) && typeof printerId === "string" && printerId.length > 0;
  useFallbackGroupsSignalRSync(enabled);

  return useQuery<FilamentFallbackGroup[]>({
    queryKey: enabled
      ? fallbackGroupsQueryKeys.byPrinter(printerId as string)
      : [...fallbackGroupsQueryKeys.all, "printer", "__disabled__"],
    queryFn: ({ signal }) => fallbackGroupsService.list(printerId as string, signal),
    enabled,
    staleTime: STALE_MS,
  });
}

export function useCreateFallbackGroup(printerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateFilamentFallbackGroupRequest) =>
      fallbackGroupsService.create(printerId, request),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: fallbackGroupsQueryKeys.byPrinter(printerId) });
    },
  });
}

export function useUpdateFallbackGroup(printerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      groupId,
      request,
    }: {
      groupId: string;
      request: UpdateFilamentFallbackGroupRequest;
    }) => fallbackGroupsService.update(printerId, groupId, request),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: fallbackGroupsQueryKeys.byPrinter(printerId) });
    },
  });
}

export function useDeleteFallbackGroup(printerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (groupId: string) => fallbackGroupsService.remove(printerId, groupId),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: fallbackGroupsQueryKeys.byPrinter(printerId) });
    },
  });
}

/**
 * Convenience helper: reorder just the members of a group by ID. Sends a
 * PUT with the new toolhead order preserving the current name/materialType.
 * The caller passes the target group (pre-mutation) plus the new ordering.
 */
export function useReorderFallbackGroupMembers(printerId: string) {
  const update = useUpdateFallbackGroup(printerId);
  return {
    ...update,
    reorder: (group: FilamentFallbackGroup, newToolheadIds: string[]) =>
      update.mutateAsync({
        groupId: group.id,
        request: {
          name: group.name,
          materialType: group.materialType,
          displayOrder: group.displayOrder,
          toolheadIds: newToolheadIds,
        },
      }),
  };
}
