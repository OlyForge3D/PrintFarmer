import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import { printerSignalRService } from '@/services/printer-signalr';

export function QueueRealtimeBridge() {
  const queryClient = useQueryClient();

  useEffect(() => {
    let disposed = false;
    let refreshInFlight: Promise<void> | null = null;

    const invalidateAuthority = async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.jobQueue() }),
        queryClient.invalidateQueries({ queryKey: ['queue-jobs'] }),
        queryClient.invalidateQueries({ queryKey: ['queue-stats'] }),
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
    };

    const refreshAuthority = () => {
      if (!refreshInFlight) {
        refreshInFlight = (async () => {
          await invalidateAuthority();
          await reconcileSubscriptions();
        })().finally(() => {
          refreshInFlight = null;
        });
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

    void printerSignalRService.connect();

    return () => {
      disposed = true;
      unsubscribeQueue();
      unsubscribeConnection();
      unsubscribeResources();
      void printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: [],
        jobIds: [],
        projectIds: [],
      });
      void printerSignalRService.disconnect();
    };
  }, [queryClient]);

  return null;
}
