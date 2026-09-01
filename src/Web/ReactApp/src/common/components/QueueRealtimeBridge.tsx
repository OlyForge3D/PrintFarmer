import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/common/hooks/queryKeys';
import { getQueueSubscriptionResources } from '@/services/api/queueRealtimeApi';
import { getPrinters } from '@/services/api/printerApi';
import { printerSignalRService } from '@/services/printer-signalr';
import { queueSummariesFleetQueryKey } from '@/features/printers/hooks/useQueueSummariesFleet';

const resourceRefreshRetryDelaysMs = [100, 250, 500] as const;

// #1731 PR #1741 review (Vasquez): the narrowed `queueresourceschanged` hint is a
// single best-effort SignalR send -- QueueSubscriptionMembershipNotifier swallows
// broadcast failures so the mutation that triggered it never fails, and removing the
// old unconditional per-outbox-event broadcast removed the incidental self-heal that
// used to paper over any dropped hint. A missed hint for an access-revoking mutation
// (printer moved out of a group the caller can no longer see, role downgrade, etc.)
// could otherwise leave an already-connected client permanently over-subscribed with
// no recovery path. This bounded periodic fallback re-runs the full reconciliation
// (and its invalidation) on a slow cadence regardless of any missed/failed hint, so a
// dropped notification is corrected within one interval instead of never. The interval
// is intentionally long -- this is defense-in-depth, not the primary correctness path,
// and must never fire within the short window covered by the "burst of ordinary
// events produces zero refetch" acceptance test.
const periodicSelfHealIntervalMs = 60_000;

// #1731: event types that can only ever reflect a single printer's physical/backend
// actuation state (bed-clear acknowledgement lifecycle, backend pause/resume/cancel
// commands). These can never add/remove a job, project, or printer from the queue, so
// when every event accumulated since the last authoritative refresh matches one of
// these, invalidateAuthority narrows its invalidation to just the printers key instead
// of the full query-key set. Any other/unknown event type falls back to the full set --
// this narrowing is deliberately conservative, since under-invalidating risks stale UI.
const printerActuationOnlyEventTypeSubstrings = [
  'BedClear',
  'BackendControl',
  'PhysicalControl',
] as const;

function isPrinterActuationOnlyEventType(eventType: string | undefined | null): boolean {
  if (!eventType) return false;
  return printerActuationOnlyEventTypeSubstrings.some((substring) =>
    eventType.includes(substring)
  );
}

/**
 * Extracted verbatim from the original single-pipeline implementation (#1731): a
 * generation-counter/retry-backoff loop that coalesces bursts of triggers into a
 * single in-flight run, retries a failed run with backoff, and restarts if a new
 * trigger arrived while retrying. Parameterized so it can independently drive the
 * invalidation pipeline and the subscription-reconciliation pipeline, which #1731
 * splits apart so that ordinary queue events only drive the former.
 */
function createCoalescedRefresher(run: () => Promise<void>, isDisposed: () => boolean) {
  let refreshInFlight: Promise<void> | null = null;
  let requestedGeneration = 0;
  let completedGeneration = 0;
  let exhaustedGeneration = 0;
  let retryTimer: ReturnType<typeof setTimeout> | undefined;
  let resolveRetryDelay: (() => void) | undefined;

  const waitForRetryDelay = (delayMs: number) =>
    new Promise<void>((resolve) => {
      resolveRetryDelay = resolve;
      retryTimer = setTimeout(() => {
        retryTimer = undefined;
        resolveRetryDelay = undefined;
        resolve();
      }, delayMs);
    });

  const startRefreshLoop = () => {
    if (refreshInFlight || isDisposed()) return;
    refreshInFlight = (async () => {
      while (
        completedGeneration < requestedGeneration &&
        exhaustedGeneration < requestedGeneration &&
        !isDisposed()
      ) {
        const targetGeneration = requestedGeneration;
        let refreshed = false;
        for (
          let attempt = 0;
          attempt <= resourceRefreshRetryDelaysMs.length && !isDisposed();
          attempt += 1
        ) {
          try {
            await run();
            completedGeneration = targetGeneration;
            refreshed = true;
            break;
          } catch (error) {
            console.error(
              '[QueueRealtimeBridge] authoritative refresh failed',
              error
            );
            if (isDisposed() || attempt === resourceRefreshRetryDelaysMs.length) {
              break;
            }
            await waitForRetryDelay(resourceRefreshRetryDelaysMs[attempt]);
          }
        }

        if (!refreshed && requestedGeneration === targetGeneration) {
          exhaustedGeneration = targetGeneration;
          return;
        }
      }
    })().finally(() => {
      refreshInFlight = null;
      if (
        completedGeneration < requestedGeneration &&
        exhaustedGeneration < requestedGeneration &&
        !isDisposed()
      ) {
        startRefreshLoop();
      }
    });
  };

  const trigger = () => {
    if (!isDisposed()) {
      requestedGeneration += 1;
      startRefreshLoop();
    }
    return refreshInFlight;
  };

  const dispose = () => {
    if (retryTimer) clearTimeout(retryTimer);
    retryTimer = undefined;
    resolveRetryDelay?.();
    resolveRetryDelay = undefined;
  };

  return { trigger, dispose };
}

export function QueueRealtimeBridge() {
  const queryClient = useQueryClient();

  useEffect(() => {
    let disposed = false;
    const isDisposed = () => disposed;

    // #1731: event types accumulated since the last invalidateAuthority run, drained
    // (and used to narrow the invalidation) at the start of each run.
    const pendingEventTypes = new Set<string>();
    // #1731: set whenever a connect/resources-changed/mount trigger requests an
    // invalidation, and consumed at the start of the next invalidateAuthority run.
    // This is deliberately independent of pendingEventTypes: if a reconnect (which may
    // have missed arbitrary events while disconnected) happens to get coalesced into
    // the same run as an in-flight actuation-only queue event, the run must still be
    // a full invalidation -- narrowing must never be inferred from *what type of
    // trigger* caused the run, only from ordinary queue events on their own.
    let forceFullInvalidation = false;

    const invalidateAuthority = async () => {
      const eventTypes = Array.from(pendingEventTypes);
      pendingEventTypes.clear();
      const mustInvalidateFully = forceFullInvalidation;
      forceFullInvalidation = false;

      if (
        !mustInvalidateFully &&
        eventTypes.length > 0 &&
        eventTypes.every(isPrinterActuationOnlyEventType)
      ) {
        // Every accumulated event since the last run is a single-printer physical/
        // backend actuation -- only that printer's own state can have changed.
        await queryClient.invalidateQueries({ queryKey: queryKeys.printers });
        return;
      }

      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.jobQueue() }),
        queryClient.invalidateQueries({ queryKey: ['queue-jobs'] }),
        queryClient.invalidateQueries({ queryKey: ['queue-stats'] }),
        // Canonical fleet queue-summary key (#1146 item 9): keeps every
        // compact printer card's "X of Y" label in step with the same
        // realtime queue events that refresh the rest of the queue surface,
        // instead of waiting out the summary query's own 30s poll.
        queryClient.invalidateQueries({ queryKey: queueSummariesFleetQueryKey }),
        queryClient.invalidateQueries({ queryKey: queryKeys.printers }),
        queryClient.invalidateQueries({ queryKey: queryKeys.scheduledJobs }),
        queryClient.invalidateQueries({ queryKey: ['auto-dispatch'] }),
      ]);
    };

    const reconcileSubscriptions = async () => {
      const [resources, printers] = await Promise.all([
        getQueueSubscriptionResources(),
        // #1731 (Vasquez review): ensureQueryData() returns already-cached data
        // unconditionally whenever it is defined, regardless of invalidation/staleness,
        // unless revalidateIfStale is set -- and even then it still returns the stale
        // value synchronously, only kicking off a background refetch for *future* reads.
        // That would silently defeat the whole point of this hint: reconcileSubscriptions
        // only runs on an actual membership-change/connect/mount trigger, so it must see
        // fresh data on exactly those occasions. fetchQuery() checks isInvalidated/staleness
        // itself and refetches when needed, while still deduping against any in-flight
        // fetch for the same key (e.g. one invalidateAuthority's own invalidateQueries just
        // triggered) and returning the cached value untouched when it is still fresh.
        queryClient.fetchQuery({
          queryKey: queryKeys.printers,
          queryFn: () => getPrinters(),
          staleTime: 30000,
        }),
      ]);
      if (disposed) return;
      await printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: [
          ...printers.map((printer) => printer.id),
          ...resources.printerIds,
        ],
        jobIds: resources.jobIds,
        projectIds: resources.projectIds,
      });
      if (!disposed) {
        await printerSignalRService.connect();
      }
    };

    const invalidateRefresher = createCoalescedRefresher(
      invalidateAuthority,
      isDisposed
    );
    const reconcileRefresher = createCoalescedRefresher(
      reconcileSubscriptions,
      isDisposed
    );

    const refreshInvalidateOnly = () => invalidateRefresher.trigger();
    const refreshBoth = () => {
      // A connect/resources-changed/mount trigger may have missed arbitrary
      // (non-actuation) events, or genuinely changed subscription membership --
      // either way its invalidation must never be narrowed to just the printers key.
      forceFullInvalidation = true;
      const invalidatePromise = invalidateRefresher.trigger();
      const reconcilePromise = reconcileRefresher.trigger();
      return Promise.all([invalidatePromise, reconcilePromise]);
    };

    const unsubscribeQueue = printerSignalRService.onQueueEvent((event) => {
      if (event.printerId) {
        void queryClient.invalidateQueries({
          queryKey: queryKeys.printer(event.printerId),
        });
      }
      if (event.jobId) {
        void queryClient.invalidateQueries({
          queryKey: queryKeys.scheduledJob(event.jobId),
        });
      }
      // #1731: ordinary queue events (job status/dispatch/bed-clear lifecycle) can
      // never change *which* printers/jobs/projects a client is subscribed to, so
      // they only drive the invalidation pipeline -- subscription reconciliation
      // (and its printers/resources refetch) is reserved for onQueueResourcesChanged.
      pendingEventTypes.add(event.eventType);
      void refreshInvalidateOnly();
    });
    const unsubscribeConnection =
      printerSignalRService.onConnectionStateChange((connected) => {
        if (connected) void refreshBoth();
      });
    const unsubscribeResources =
      printerSignalRService.onQueueResourcesChanged?.(() => {
        void refreshBoth();
      }) ?? (() => {});

    void refreshBoth();
    void printerSignalRService.connect();

    // #1731 (Vasquez review): bounded periodic self-heal -- see
    // periodicSelfHealIntervalMs above.
    const selfHealTimer = setInterval(() => {
      if (!disposed) void refreshBoth();
    }, periodicSelfHealIntervalMs);

    return () => {
      disposed = true;
      clearInterval(selfHealTimer);
      unsubscribeQueue();
      unsubscribeConnection();
      unsubscribeResources();
      invalidateRefresher.dispose();
      reconcileRefresher.dispose();
      const lifecycle = printerSignalRService as typeof printerSignalRService & {
        releaseQueueResourceSubscriptionsAndDisconnect?: () => Promise<void>;
      };
      const cleanup = lifecycle.releaseQueueResourceSubscriptionsAndDisconnect
        ? lifecycle.releaseQueueResourceSubscriptionsAndDisconnect()
        : (async () => {
            const releaseGeneration =
              await printerSignalRService.replaceQueueResourceSubscriptions({
                printerIds: [],
                jobIds: [],
                projectIds: [],
              });
            await printerSignalRService.disconnect(releaseGeneration);
          })();
      void cleanup.catch((error) => {
          console.error('[QueueRealtimeBridge] cleanup failed', error);
        });
    };
  }, [queryClient]);

  return null;
}
