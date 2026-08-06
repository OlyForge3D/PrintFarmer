import { StrictMode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { QueueRealtimeBridge } from '../QueueRealtimeBridge';
import { queueSummariesFleetQueryKey } from '@/features/printers/hooks/useQueueSummariesFleet';

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((next) => {
    resolve = next;
  });
  return { promise, resolve };
}

const mocks = vi.hoisted(() => {
  let queueCallback:
    | ((event: { jobId?: string; printerId?: string }) => void)
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
      (callback: (event: { jobId?: string; printerId?: string }) => void) => {
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
    emitQueueEvent: (event: { jobId?: string; printerId?: string }) =>
      queueCallback?.(event),
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
