/**
 * React Query hooks for filament coverage (issue #717).
 *
 * - `useFleetFilamentCoverage()` powers grid-style surfaces (single call
 *   feeds every card). Individual card hooks derive from the same query
 *   via `select` so we don't fan out N+1 requests.
 * - `usePrinterFilamentCoverage(printerId)` powers per-printer detail
 *   surfaces (details sidebar). It shares the fleet snapshot when
 *   present and falls back to the single-printer endpoint otherwise.
 * - SignalR `filamentcoveragechanged` cues are attached exactly once per
 *   mounted `QueryClient`; they invalidate the canonical queries so the
 *   next render refetches the truth from the API.
 */
import { useEffect } from "react";
import {
  useQuery,
  useQueryClient,
  type QueryClient,
  type UseQueryOptions,
  type UseQueryResult,
} from "@tanstack/react-query";

import { printerSignalRService } from "@/services/printer-signalr";
import { filamentCoverageService } from "./service";
import type {
  FilamentCoverageChangedEvent,
  FleetFilamentCoverage,
  PrinterFilamentCoverage,
} from "./types";

const FLEET_STALE_MS = 15_000;
const FLEET_REFETCH_MS = 30_000;

export const filamentCoverageQueryKeys = {
  all: ["filament-coverage"] as const,
  fleet: () => [...filamentCoverageQueryKeys.all, "fleet"] as const,
  printer: (printerId: string) =>
    [...filamentCoverageQueryKeys.all, "printer", printerId] as const,
};

/**
 * Reference-counted map of query clients listening for coverage change events.
 * A single `QueryClient` singleton (the common case) can be mounted by many hooks
 * simultaneously; the count tracks how many are active so we only unsubscribe from
 * SignalR when the last consumer unmounts.
 */
const coverageQueryClients = new Map<QueryClient, number>();
let coverageSignalRUnsubscribe: (() => void) | undefined;

function invalidateCoverageCaches(event: FilamentCoverageChangedEvent) {
  coverageQueryClients.forEach((_count, qc) => {
    // Fleet cache is always affected — either a single printer changed
    // (its slot in the batch response) or the whole fleet did.
    void qc.invalidateQueries({ queryKey: filamentCoverageQueryKeys.fleet() });
    if (event.printerId) {
      void qc.invalidateQueries({
        queryKey: filamentCoverageQueryKeys.printer(event.printerId),
      });
    } else {
      // Fleet-wide invalidation: refetch every per-printer subscription too.
      void qc.invalidateQueries({
        queryKey: [...filamentCoverageQueryKeys.all, "printer"],
      });
    }
  });
}

function ensureCoverageSignalRSubscription() {
  if (coverageSignalRUnsubscribe) return;
  void printerSignalRService.connect();
  coverageSignalRUnsubscribe = printerSignalRService.onFilamentCoverageChanged(
    invalidateCoverageCaches,
  );
}

function useCoverageSignalRSync() {
  const queryClient = useQueryClient();
  useEffect(() => {
    const prev = coverageQueryClients.get(queryClient) ?? 0;
    coverageQueryClients.set(queryClient, prev + 1);
    ensureCoverageSignalRSubscription();
    return () => {
      const remaining = (coverageQueryClients.get(queryClient) ?? 1) - 1;
      if (remaining <= 0) {
        coverageQueryClients.delete(queryClient);
      } else {
        coverageQueryClients.set(queryClient, remaining);
      }
      if (coverageQueryClients.size === 0 && coverageSignalRUnsubscribe) {
        coverageSignalRUnsubscribe();
        coverageSignalRUnsubscribe = undefined;
      }
    };
  }, [queryClient]);
}

/** Test-only reset for module-level SignalR wiring. */
export function __resetFilamentCoverageSubscriptionForTests(): void {
  if (coverageSignalRUnsubscribe) {
    coverageSignalRUnsubscribe();
    coverageSignalRUnsubscribe = undefined;
  }
  coverageQueryClients.clear();
}

export interface UseFleetFilamentCoverageOptions {
  /** When false, disables both the query and the SignalR subscription. */
  enabled?: boolean;
}

/**
 * Fleet coverage query. Returns `null` when the feature is disabled
 * (server responded with 404).
 */
export function useFleetFilamentCoverage(
  options: UseFleetFilamentCoverageOptions = {},
): UseQueryResult<FleetFilamentCoverage | null> {
  const enabled = options.enabled ?? true;
  useCoverageSignalRSync();
  const queryOptions: UseQueryOptions<FleetFilamentCoverage | null> = {
    queryKey: filamentCoverageQueryKeys.fleet(),
    queryFn: ({ signal }) => filamentCoverageService.getFleetCoverage(signal),
    staleTime: FLEET_STALE_MS,
    refetchInterval: FLEET_REFETCH_MS,
    enabled,
  };
  return useQuery(queryOptions);
}

/**
 * Per-printer coverage lookup. Derives from the fleet snapshot when it
 * already contains the printer; otherwise fetches the single-printer
 * endpoint directly. Returns `null` when the feature is disabled.
 */
export function usePrinterFilamentCoverage(
  printerId: string | null | undefined,
): UseQueryResult<PrinterFilamentCoverage | null> {
  useCoverageSignalRSync();
  const qc = useQueryClient();
  const enabled = typeof printerId === "string" && printerId.length > 0;

  const queryOptions: UseQueryOptions<PrinterFilamentCoverage | null> = {
    queryKey: enabled
      ? filamentCoverageQueryKeys.printer(printerId)
      : [...filamentCoverageQueryKeys.all, "printer", "__disabled__"],
    queryFn: async ({ signal }) => {
      if (!enabled) return null;
      // Only reuse the fleet cache when it is present, fresh, and not mid-flight.
      // If the fleet query is invalidated or currently fetching its refetch, its
      // cached data may be stale relative to the event that triggered this call,
      // so we fall through to the per-printer endpoint to guarantee freshness.
      const fleetState = qc.getQueryState(filamentCoverageQueryKeys.fleet());
      if (
        fleetState &&
        !fleetState.isInvalidated &&
        fleetState.fetchStatus !== "fetching"
      ) {
        const fleet = qc.getQueryData<FleetFilamentCoverage | null>(
          filamentCoverageQueryKeys.fleet(),
        );
        if (fleet && Array.isArray(fleet.printers)) {
          const hit = fleet.printers.find((p) => p.printerId === printerId);
          if (hit) return hit;
        }
      }
      return filamentCoverageService.getPrinterCoverage(printerId, signal);
    },
    staleTime: FLEET_STALE_MS,
    refetchInterval: FLEET_REFETCH_MS,
    enabled,
  };
  return useQuery(queryOptions);
}

/**
 * Convenience selector for grid surfaces: given the fleet query result,
 * returns the coverage entry for `printerId` if known. Never invents
 * data — if the fleet snapshot doesn't contain the printer this returns
 * `undefined` (the caller should treat it as "unknown").
 */
export function selectPrinterFromFleet(
  fleet: FleetFilamentCoverage | null | undefined,
  printerId: string,
): PrinterFilamentCoverage | undefined {
  if (!fleet || !Array.isArray(fleet.printers)) return undefined;
  return fleet.printers.find((p) => p.printerId === printerId);
}
