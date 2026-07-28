import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { QueueRealtimeBridge } from '../QueueRealtimeBridge';

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
});
