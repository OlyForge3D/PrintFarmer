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
import { useCallback, useEffect } from "react";
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

/**
 * Fleet-cache invalidation is throttled to at most once per this window
 * (leading + trailing edge), instead of forcing a synchronous refetch on
 * every per-printer tick (issue #2375). Backend polling cadence is ~5s per
 * printer, so a 2s window still coalesces same-printer bursts and staggered
 * multi-printer ticks into far fewer fleet recomputes, while staying well
 * under the 15s fleet staleness budget so coverage never reads stale for
 * longer than operators would tolerate. Exported so tests can assert against
 * the real window instead of a hardcoded duplicate.
 */
export const FLEET_INVALIDATE_THROTTLE_MS = 2_000;

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

interface FleetInvalidationState {
  lastInvalidatedAt: number;
  trailingTimer: ReturnType<typeof setTimeout> | undefined;
  hasPendingTrailing: boolean;
}

/** Per-QueryClient throttle bookkeeping. A plain Map (not WeakMap) so tests can clear it. */
const fleetInvalidationState = new Map<QueryClient, FleetInvalidationState>();

function getFleetInvalidationState(qc: QueryClient): FleetInvalidationState {
  let state = fleetInvalidationState.get(qc);
  if (!state) {
    state = { lastInvalidatedAt: 0, trailingTimer: undefined, hasPendingTrailing: false };
    fleetInvalidationState.set(qc, state);
  }
  return state;
}

/**
 * Invalidates the fleet coverage cache at most once per
 * `FLEET_INVALIDATE_THROTTLE_MS`. The first invalidation in a burst fires
 * immediately (leading edge) so a lone event is never delayed; subsequent
 * events within the window are coalesced into exactly one trailing
 * invalidation once the window closes, so fleet coverage still reflects the
 * latest change shortly after a burst settles instead of going stale
 * indefinitely.
 */
function invalidateFleetThrottled(qc: QueryClient): void {
  const state = getFleetInvalidationState(qc);
  const now = Date.now();
  const elapsed = now - state.lastInvalidatedAt;
  if (elapsed >= FLEET_INVALIDATE_THROTTLE_MS) {
    state.lastInvalidatedAt = now;
    void qc.invalidateQueries({ queryKey: filamentCoverageQueryKeys.fleet() });
    return;
  }

  state.hasPendingTrailing = true;
  if (state.trailingTimer !== undefined) return;
  const remaining = FLEET_INVALIDATE_THROTTLE_MS - elapsed;
  state.trailingTimer = setTimeout(() => {
    state.trailingTimer = undefined;
    if (!state.hasPendingTrailing) return;
    state.hasPendingTrailing = false;
    state.lastInvalidatedAt = Date.now();
    void qc.invalidateQueries({ queryKey: filamentCoverageQueryKeys.fleet() });
  }, remaining);
}

function invalidateCoverageCaches(event: FilamentCoverageChangedEvent) {
  coverageQueryClients.forEach((_count, qc) => {
    if (event.printerId) {
      // Per-printer tick: only that printer's slice of the fleet snapshot
      // changed, so the fleet refetch is throttled/coalesced rather than
      // forced on every event (issue #2375). Per-printer invalidation stays
      // immediate: it is cheap (a single printer's slice) and drives the
      // detail view's own live update.
      invalidateFleetThrottled(qc);
      void qc.invalidateQueries({
        queryKey: filamentCoverageQueryKeys.printer(event.printerId),
      });
    } else {
      // Genuine fleet-scope event (spool swap, printer added/removed): the
      // whole fleet snapshot is authoritative and must never be delayed by
      // the per-printer throttle, or fleet-scope delivery would regress
      // (issue #2375 acceptance criteria). Invalidate immediately and reset
      // the throttle window so it doesn't suppress the *next* per-printer
      // tick's leading edge.
      const state = getFleetInvalidationState(qc);
      if (state.trailingTimer !== undefined) {
        clearTimeout(state.trailingTimer);
        state.trailingTimer = undefined;
      }
      state.hasPendingTrailing = false;
      state.lastInvalidatedAt = Date.now();
      void qc.invalidateQueries({ queryKey: filamentCoverageQueryKeys.fleet() });
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

function useCoverageSignalRSync(enabled: boolean) {
  const queryClient = useQueryClient();
  useEffect(() => {
    if (!enabled) return;

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
  }, [enabled, queryClient]);
}

/** Test-only reset for module-level SignalR wiring. */
export function __resetFilamentCoverageSubscriptionForTests(): void {
  if (coverageSignalRUnsubscribe) {
    coverageSignalRUnsubscribe();
    coverageSignalRUnsubscribe = undefined;
  }
  coverageQueryClients.clear();
  for (const state of fleetInvalidationState.values()) {
    if (state.trailingTimer !== undefined) {
      clearTimeout(state.trailingTimer);
    }
  }
  fleetInvalidationState.clear();
}

export interface UseFleetFilamentCoverageOptions<
  TData = FleetFilamentCoverage | null,
> {
  /** When false, disables both the query and the SignalR subscription. */
  enabled?: boolean;
  select?: (fleet: FleetFilamentCoverage | null) => TData;
}

/**
 * Fleet coverage query. Returns `null` when the feature is disabled
 * (server responded with 404).
 */
export function useFleetFilamentCoverage<
  TData = FleetFilamentCoverage | null,
>(
  options: UseFleetFilamentCoverageOptions<TData> = {},
): UseQueryResult<TData> {
  const enabled = options.enabled ?? true;
  useCoverageSignalRSync(enabled);
  const queryOptions: UseQueryOptions<
    FleetFilamentCoverage | null,
    Error,
    TData
  > = {
    queryKey: filamentCoverageQueryKeys.fleet(),
    queryFn: ({ signal }) => filamentCoverageService.getFleetCoverage(signal),
    staleTime: FLEET_STALE_MS,
    refetchInterval: FLEET_REFETCH_MS,
    enabled,
    select: options.select,
  };
  return useQuery(queryOptions);
}

/**
 * Per-printer selector for grid surfaces. Every consumer shares the fleet
 * query key, so concurrent cards produce one deduplicated fleet request.
 */
export function usePrinterCoverageFromFleet(
  printerId: string,
  options: Pick<UseFleetFilamentCoverageOptions, "enabled"> = {},
): Pick<
  UseQueryResult<PrinterFilamentCoverage | null>,
  "data" | "isPending" | "isError" | "error"
> {
  const select = useCallback(
    (fleet: FleetFilamentCoverage | null) =>
      fleet
        ? (fleet.printers.find((printer) => printer.printerId === printerId) ?? null)
        : null,
    [printerId],
  );
  const { data, isPending, isError, error } = useFleetFilamentCoverage({
    ...options,
    select,
  });

  return { data, isPending, isError, error };
}

/**
 * Per-printer coverage lookup. Derives from the fleet snapshot when it
 * already contains the printer; otherwise fetches the single-printer
 * endpoint directly. Returns `null` when the feature is disabled.
 */
export function usePrinterFilamentCoverage(
  printerId: string | null | undefined,
): UseQueryResult<PrinterFilamentCoverage | null> {
  const enabled = typeof printerId === "string" && printerId.length > 0;
  useCoverageSignalRSync(enabled);
  const qc = useQueryClient();

  const queryOptions: UseQueryOptions<PrinterFilamentCoverage | null> = {
    queryKey: enabled
      ? filamentCoverageQueryKeys.printer(printerId)
      : [...filamentCoverageQueryKeys.all, "printer", "__disabled__"],
    queryFn: async ({ signal }) => {
      if (!enabled) return null;
      const fleetState = qc.getQueryState<FleetFilamentCoverage | null>(
        filamentCoverageQueryKeys.fleet(),
      );
      const hasFreshFleetData =
        fleetState?.data !== undefined &&
        fleetState.fetchStatus === "idle" &&
        Date.now() - fleetState.dataUpdatedAt <= FLEET_STALE_MS;
      if (hasFreshFleetData && fleetState.data) {
        const hit = fleetState.data.printers.find((p) => p.printerId === printerId);
        if (hit) return hit;
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
