import { StrictMode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { QueueRealtimeBridge } from '../QueueRealtimeBridge';
import { queueSummariesFleetQueryKey } from '@/features/printers/hooks/useQueueSummariesFleet';
import { queryKeys } from '@/common/hooks/useApi';

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((next) => {
    resolve = next;
  });
  return { promise, resolve };
}

const mocks = vi.hoisted(() => {
  let queueCallback:
    | ((event: { jobId?: string; printerId?: string; eventType?: string }) => void)
    | undefined;
  let connectionCallback: ((connected: boolean) => void) | undefined;
  let resourcesChangedCallback: (() => void) | undefined;
  return {
    getQueueSubscriptionResources: vi.fn(),
    getPrinters: vi.fn(),
    replaceQueueResourceSubscriptions: vi.fn().mockResolvedValue(undefined),
    releaseQueueResourceSubscriptionsAndDisconnect:
      vi.fn().mockResolvedValue(undefined),
    connect: vi.fn().mockResolvedValue(undefined),
    disconnect: vi.fn().mockResolvedValue(undefined),
    onQueueEvent: vi.fn(
      (
        callback: (event: {
          jobId?: string;
          printerId?: string;
          eventType?: string;
        }) => void
      ) => {
        queueCallback = callback;
        return vi.fn();
      }
    ),
    onConnectionStateChange: vi.fn(
      (callback: (connected: boolean) => void) => {
        connectionCallback = callback;
        return vi.fn();
      }
    ),
    onQueueResourcesChanged: vi.fn((callback: () => void) => {
      resourcesChangedCallback = callback;
      return vi.fn();
    }),
    // Defaults to a generic, non-actuation-only event type -- matching real
    // ordinary queue events (job dispatched/completed/etc.) that #1731's
    // narrowing must still fall back to the full invalidation set for.
    emitQueueEvent: (event: {
      jobId?: string;
      printerId?: string;
      eventType?: string;
    }) =>
      queueCallback?.({
        eventType: 'PrintFarmer.Queue.JobDispatchStarted.v1',
        ...event,
      }),
    emitConnection: (connected: boolean) => connectionCallback?.(connected),
    emitResourcesChanged: () => resourcesChangedCallback?.(),
  };
});

vi.mock('@/services/api', () => ({
  apiClient: {
    getQueueSubscriptionResources: mocks.getQueueSubscriptionResources,
    getPrinters: mocks.getPrinters,
  },
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    replaceQueueResourceSubscriptions:
      mocks.replaceQueueResourceSubscriptions,
    releaseQueueResourceSubscriptionsAndDisconnect:
      mocks.releaseQueueResourceSubscriptionsAndDisconnect,
    connect: mocks.connect,
    disconnect: mocks.disconnect,
    onQueueEvent: mocks.onQueueEvent,
    onConnectionStateChange: mocks.onConnectionStateChange,
    onQueueResourcesChanged: mocks.onQueueResourcesChanged,
  },
}));

describe('QueueRealtimeBridge', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.getQueueSubscriptionResources.mockResolvedValue({
      printerIds: ['assigned-printer'],
      jobIds: ['job-1'],
      projectIds: ['project-1'],
    });
    mocks.getPrinters.mockResolvedValue([{ id: 'visible-printer' }]);
  });

  it('discovers a post-connect resource from the server hint and refetches queue keys', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );

    mocks.emitConnection(true);
    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledWith({
        printerIds: ['visible-printer', 'assigned-printer'],
        jobIds: ['job-1'],
        projectIds: ['project-1'],
      })
    );
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['queue-jobs'] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['queue-stats'] });
    // Canonical fleet queue-summary key (#1146 item 9): every compact
    // printer card's "X of Y" label shares this key, so a connection-driven
    // authoritative refresh must refresh it too, not just job-queue/queue-jobs/queue-stats.
    expect(invalidate).toHaveBeenCalledWith({ queryKey: queueSummariesFleetQueryKey });

    mocks.getQueueSubscriptionResources.mockResolvedValue({
      printerIds: ['new-printer'],
      jobIds: ['job-2'],
      projectIds: ['project-2'],
    });
    mocks.emitResourcesChanged();

    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenLastCalledWith({
        printerIds: ['visible-printer', 'new-printer'],
        jobIds: ['job-2'],
        projectIds: ['project-2'],
      })
    );
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['queue-jobs'] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['queue-stats'] });
  });

  it('invalidates the canonical fleet queue-summary key on a realtime queue event, in addition to the connection/resources-changed paths', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );

    // A queue event (job dispatched/completed/etc.) is a distinct trigger
    // from the initial connection and the resources-changed hint — it must
    // independently refresh the fleet queue-summary key too.
    mocks.emitQueueEvent({ printerId: 'assigned-printer', jobId: 'job-1' });

    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({ queryKey: queueSummariesFleetQueryKey })
    );
  });

  it('#1731: a burst of ordinary queue events does not reconcile subscriptions or refetch printers/resources beyond the initial mount reconcile', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );

    // Initial mount reconciles once (unchanged from today).
    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledTimes(1)
    );
    mocks.getQueueSubscriptionResources.mockClear();
    mocks.getPrinters.mockClear();
    mocks.replaceQueueResourceSubscriptions.mockClear();

    // A burst of ordinary queue events (job dispatched/completed/etc.) that do NOT
    // change subscription membership must produce zero additional reconciliations.
    mocks.emitQueueEvent({ printerId: 'p1', jobId: 'job-1' });
    mocks.emitQueueEvent({ printerId: 'p2', jobId: 'job-2' });
    mocks.emitQueueEvent({ printerId: 'p3', jobId: 'job-3' });

    await waitFor(() => {
      expect(mocks.getQueueSubscriptionResources).not.toHaveBeenCalled();
      expect(mocks.getPrinters).not.toHaveBeenCalled();
      expect(mocks.replaceQueueResourceSubscriptions).not.toHaveBeenCalled();
    });
  });

  it('#1731: reassigning a printer to a different group (resources-changed hint) still reconciles exactly once with the correct resulting subscription set', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );

    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledTimes(1)
    );
    mocks.replaceQueueResourceSubscriptions.mockClear();
    mocks.getQueueSubscriptionResources.mockClear();

    // A burst of ordinary (non-membership) queue events first -- must not reconcile.
    mocks.emitQueueEvent({ printerId: 'p1' });
    mocks.emitQueueEvent({ printerId: 'p1' });

    // Now the printer is reassigned to a different group server-side, which is the
    // one case that DOES change subscription membership and must be reconciled --
    // under-emitting this hint would leave the client subscribed to the wrong set.
    mocks.getQueueSubscriptionResources.mockResolvedValue({
      printerIds: ['reassigned-printer'],
      jobIds: ['job-x'],
      projectIds: ['project-x'],
    });
    mocks.emitResourcesChanged();

    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledWith({
        printerIds: ['visible-printer', 'reassigned-printer'],
        jobIds: ['job-x'],
        projectIds: ['project-x'],
      })
    );
    expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledTimes(1);
    expect(mocks.getQueueSubscriptionResources).toHaveBeenCalledTimes(1);
  });

  it('#1731: reconciliation dedupes its printers fetch against an already in-flight fetch for the same key, instead of issuing a second parallel GET', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );
    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledTimes(1)
    );
    expect(mocks.getPrinters).toHaveBeenCalledTimes(1);

    // Every reconciliation trigger (resources-changed/connect/mount) is always paired
    // with a *forced* full invalidation of the printers key (see forceFullInvalidation),
    // so reconciliation always sees printers data as invalidated and must genuinely
    // refetch it -- see the "invalidated" test above. What #1731's switch to
    // queryClient.fetchQuery() actually buys over a raw apiClient.getPrinters() call is
    // deduping with a fetch *already in flight* for the same key -- e.g. an app page
    // using usePrinters() that reacted to the very same invalidation. Simulate that here:
    // start a fetch for the printers key ourselves (standing in for that other consumer)
    // and hold it open, then trigger the reconciliation and confirm it reuses that
    // single in-flight request rather than calling getPrinters() again.
    const inFlightPrinters = deferred<{ id: string }[]>();
    mocks.getPrinters.mockReturnValueOnce(inFlightPrinters.promise);
    await queryClient.invalidateQueries({ queryKey: queryKeys.printers });
    const concurrentFetch = queryClient.fetchQuery({
      queryKey: queryKeys.printers,
      queryFn: mocks.getPrinters,
      staleTime: 30000,
    });

    mocks.emitResourcesChanged();
    inFlightPrinters.resolve([{ id: 'visible-printer' }]);
    await concurrentFetch;
    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledTimes(2)
    );
    expect(mocks.getPrinters).toHaveBeenCalledTimes(2);
  });

  it('#1731 (Vasquez review): reconciliation refetches printers when the cache was invalidated, instead of silently reusing stale data', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );

    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledTimes(1)
    );
    expect(mocks.getPrinters).toHaveBeenCalledTimes(1);

    // Simulate invalidateAuthority's own invalidateQueries call for the printers key
    // (e.g. from an unrelated invalidation run) marking the cached entry stale/invalidated,
    // without clearing its cached data. A membership-change hint's reconciliation must
    // still see this and refetch -- reusing ensureQueryData()'s unconditional
    // return-cached-data-if-defined behavior here would silently serve the stale printer
    // list forever.
    await queryClient.invalidateQueries({ queryKey: queryKeys.printers });
    mocks.getPrinters.mockResolvedValueOnce([{ id: 'newly-authorized-printer' }]);

    mocks.emitResourcesChanged();
    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledTimes(2)
    );
    expect(mocks.getPrinters).toHaveBeenCalledTimes(2);
    expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenLastCalledWith(
      expect.objectContaining({
        printerIds: expect.arrayContaining(['newly-authorized-printer']),
      })
    );
  });

  it('#1731: a burst of only actuation-only events (bed-clear/backend-control) invalidates just the printers key', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );
    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledTimes(1)
    );

    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    invalidate.mockClear();

    mocks.emitQueueEvent({
      printerId: 'p1',
      eventType: 'PrintFarmer.Queue.BedClearAcknowledged.v1',
    });
    mocks.emitQueueEvent({
      printerId: 'p1',
      eventType: 'PrintFarmer.Queue.BackendControlRejected.v1',
    });

    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({ queryKey: queryKeys.printers })
    );
    expect(invalidate).not.toHaveBeenCalledWith({
      queryKey: queueSummariesFleetQueryKey,
    });
    expect(invalidate).not.toHaveBeenCalledWith({ queryKey: ['queue-stats'] });
  });

  it('#1731: a mixed burst (actuation-only + an ordinary event) still invalidates the full key set', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );
    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledTimes(1)
    );

    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    invalidate.mockClear();

    mocks.emitQueueEvent({
      printerId: 'p1',
      eventType: 'PrintFarmer.Queue.BedClearAcknowledged.v1',
    });
    mocks.emitQueueEvent({
      printerId: 'p1',
      eventType: 'PrintFarmer.Queue.JobDispatchStarted.v1',
    });

    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({
        queryKey: queueSummariesFleetQueryKey,
      })
    );
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['queue-stats'] });
  });

  it('#1731: a resources-changed hint that races with a pending actuation-only queue event still forces a full invalidation', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );
    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledTimes(1)
    );

    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    invalidate.mockClear();

    // Fire an actuation-only queue event and the membership-changed hint back to
    // back, synchronously, so the coalescing loop may batch both triggers into the
    // same invalidateAuthority run. Even so, the resources-changed hint must force a
    // full invalidation -- narrowing must never be inferred from what accumulated in
    // pendingEventTypes when a connect/resources-changed/mount trigger is also in
    // play, since that trigger may have missed arbitrary non-actuation events.
    mocks.emitQueueEvent({
      printerId: 'p1',
      eventType: 'PrintFarmer.Queue.BedClearAcknowledged.v1',
    });
    mocks.emitResourcesChanged();

    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['queue-stats'] })
    );
  });

  it('runs a trailing snapshot when B commits after in-flight snapshot A reads resources', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const firstSubscriptionApply = deferred<void>();
    mocks.getQueueSubscriptionResources
      .mockResolvedValueOnce({
        printerIds: ['printer-a'],
        jobIds: ['job-a'],
        projectIds: ['project-a'],
      })
      .mockResolvedValueOnce({
        printerIds: ['printer-b'],
        jobIds: ['job-b'],
        projectIds: ['project-b'],
      });
    mocks.replaceQueueResourceSubscriptions
      .mockImplementationOnce(() => firstSubscriptionApply.promise)
      .mockResolvedValue(undefined);
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );

    mocks.emitConnection(true);
    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledWith({
        printerIds: ['visible-printer', 'printer-a'],
        jobIds: ['job-a'],
        projectIds: ['project-a'],
      })
    );

    mocks.emitResourcesChanged();
    expect(mocks.getQueueSubscriptionResources).toHaveBeenCalledTimes(1);
    firstSubscriptionApply.resolve();

    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenLastCalledWith({
        printerIds: ['visible-printer', 'printer-b'],
        jobIds: ['job-b'],
        projectIds: ['project-b'],
      })
    );
    expect(mocks.getQueueSubscriptionResources).toHaveBeenCalledTimes(2);
  });

  it('retries a lone failed authoritative refresh and applies recovered resources', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const error = vi.spyOn(console, 'error').mockImplementation(() => {});
    mocks.getQueueSubscriptionResources
      .mockRejectedValueOnce(new Error('snapshot unavailable'))
      .mockResolvedValueOnce({
        printerIds: ['recovered-printer'],
        jobIds: ['recovered-job'],
        projectIds: ['recovered-project'],
      });
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );

    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledWith({
        printerIds: ['visible-printer', 'recovered-printer'],
        jobIds: ['recovered-job'],
        projectIds: ['recovered-project'],
      })
    );
    expect(mocks.getQueueSubscriptionResources).toHaveBeenCalledTimes(2);
    expect(error).toHaveBeenCalledWith(
      '[QueueRealtimeBridge] authoritative refresh failed',
      expect.any(Error)
    );
    error.mockRestore();
  });

  it('bounds failed retries and lets a later hint restart recovery', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const error = vi.spyOn(console, 'error').mockImplementation(() => {});
    mocks.getQueueSubscriptionResources.mockRejectedValue(
      new Error('snapshot unavailable')
    );
    render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );

    await waitFor(
      () =>
        expect(mocks.getQueueSubscriptionResources).toHaveBeenCalledTimes(4),
      { timeout: 2_000 }
    );
    await new Promise((resolve) => setTimeout(resolve, 150));
    expect(mocks.getQueueSubscriptionResources).toHaveBeenCalledTimes(4);

    mocks.getQueueSubscriptionResources.mockResolvedValue({
      printerIds: ['later-printer'],
      jobIds: ['later-job'],
      projectIds: ['later-project'],
    });
    mocks.emitResourcesChanged();

    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledWith({
        printerIds: ['visible-printer', 'later-printer'],
        jobIds: ['later-job'],
        projectIds: ['later-project'],
      })
    );
    expect(mocks.getQueueSubscriptionResources).toHaveBeenCalledTimes(5);
    error.mockRestore();
  });

  it('releases ownership and disconnects when unmounted during an apply', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const inFlightApply = deferred<void>();
    mocks.replaceQueueResourceSubscriptions.mockImplementationOnce(
      () => inFlightApply.promise
    );
    const rendered = render(
      <QueryClientProvider client={queryClient}>
        <QueueRealtimeBridge />
      </QueryClientProvider>
    );
    mocks.emitConnection(true);
    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledWith({
        printerIds: ['visible-printer', 'assigned-printer'],
        jobIds: ['job-1'],
        projectIds: ['project-1'],
      })
    );

    rendered.unmount();
    await waitFor(() =>
      expect(
        mocks.releaseQueueResourceSubscriptionsAndDisconnect
      ).toHaveBeenCalledOnce()
    );
    inFlightApply.resolve();
  });

  it('keeps the latest mounted owner in React StrictMode', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <StrictMode>
        <QueryClientProvider client={queryClient}>
          <QueueRealtimeBridge />
        </QueryClientProvider>
      </StrictMode>
    );

    mocks.emitConnection(true);

    await waitFor(() =>
      expect(mocks.replaceQueueResourceSubscriptions).toHaveBeenCalledWith({
        printerIds: ['visible-printer', 'assigned-printer'],
        jobIds: ['job-1'],
        projectIds: ['project-1'],
      })
    );
    expect(mocks.onQueueEvent).toHaveBeenCalledTimes(2);
  });
});
