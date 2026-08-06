import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import { printerSignalRService } from '@/services/printer-signalr';
import { queueSummariesFleetQueryKey } from '@/features/printers/hooks/useQueueSummariesFleet';

const resourceRefreshRetryDelaysMs = [100, 250, 500] as const;

export function QueueRealtimeBridge() {
  const queryClient = useQueryClient();

  useEffect(() => {
    let disposed = false;
    let refreshInFlight: Promise<void> | null = null;
    let requestedRefreshGeneration = 0;
    let completedRefreshGeneration = 0;
    let exhaustedRefreshGeneration = 0;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    let resolveRetryDelay: (() => void) | undefined;

    const invalidateAuthority = async () => {
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
        apiClient.getQueueSubscriptionResources(),
        apiClient.getPrinters(),
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
      if (refreshInFlight || disposed) return;
      refreshInFlight = (async () => {
        while (
          completedRefreshGeneration < requestedRefreshGeneration &&
          exhaustedRefreshGeneration < requestedRefreshGeneration &&
          !disposed
        ) {
          const targetGeneration = requestedRefreshGeneration;
          let refreshed = false;
          for (
            let attempt = 0;
            attempt <= resourceRefreshRetryDelaysMs.length && !disposed;
            attempt += 1
          ) {
            try {
              await invalidateAuthority();
              await reconcileSubscriptions();
              completedRefreshGeneration = targetGeneration;
              refreshed = true;
              break;
            } catch (error) {
              console.error(
                '[QueueRealtimeBridge] authoritative refresh failed',
                error
              );
              if (
                disposed ||
                attempt === resourceRefreshRetryDelaysMs.length
              ) {
                break;
              }
              await waitForRetryDelay(
                resourceRefreshRetryDelaysMs[attempt]
              );
            }
          }

          if (!refreshed && requestedRefreshGeneration === targetGeneration) {
            exhaustedRefreshGeneration = targetGeneration;
            return;
          }
        }
      })().finally(() => {
        refreshInFlight = null;
        if (
          completedRefreshGeneration < requestedRefreshGeneration &&
          exhaustedRefreshGeneration < requestedRefreshGeneration &&
          !disposed
        ) {
          startRefreshLoop();
        }
      });
    };

    const refreshAuthority = () => {
      if (!disposed) {
        requestedRefreshGeneration += 1;
        startRefreshLoop();
      }
      return refreshInFlight;
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
      void refreshAuthority();
    });
    const unsubscribeConnection =
      printerSignalRService.onConnectionStateChange((connected) => {
        if (connected) void refreshAuthority();
      });
    const unsubscribeResources =
      printerSignalRService.onQueueResourcesChanged?.(() => {
        void refreshAuthority();
      }) ?? (() => {});

    void refreshAuthority();
    void printerSignalRService.connect();

    return () => {
      disposed = true;
      unsubscribeQueue();
      unsubscribeConnection();
      unsubscribeResources();
      if (retryTimer) clearTimeout(retryTimer);
      retryTimer = undefined;
      resolveRetryDelay?.();
      resolveRetryDelay = undefined;
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
