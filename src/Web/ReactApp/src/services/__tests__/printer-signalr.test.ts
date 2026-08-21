import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AutoDispatchStatus } from '@/types/api';

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((next) => {
    resolve = next;
  });
  return { promise, resolve };
}

const signalRTestState = vi.hoisted(() => {
  const connectionHandlers = new Map<string, (...args: unknown[]) => void>();
  let closeHandler: (() => void) | undefined;
  let reconnectingHandler: (() => void) | undefined;
  let reconnectedHandler: (() => void) | undefined;
  const connection = {
    on: vi.fn((eventName: string, callback: (...args: unknown[]) => void) => {
      connectionHandlers.set(eventName, callback);
    }),
    onclose: vi.fn((callback: () => void) => {
      closeHandler = callback;
    }),
    onreconnecting: vi.fn((callback: () => void) => {
      reconnectingHandler = callback;
    }),
    onreconnected: vi.fn((callback: () => void) => {
      reconnectedHandler = callback;
    }),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    invoke: vi.fn().mockResolvedValue(undefined),
    state: 'Disconnected',
    connectionId: null,
  };

  const builder = {
    withUrl: vi.fn(),
    withAutomaticReconnect: vi.fn(),
    configureLogging: vi.fn(),
    build: vi.fn(),
  };

  builder.withUrl.mockReturnValue(builder);
  builder.withAutomaticReconnect.mockReturnValue(builder);
  builder.configureLogging.mockReturnValue(builder);
  builder.build.mockReturnValue(connection);

  return {
    connectionHandlers,
    connection,
    builder,
    getSettings: vi.fn(),
    getQueueChanges: vi.fn(),
    getQueueChangeWatermark: vi.fn(),
    triggerClose: () => closeHandler?.(),
    triggerReconnecting: () => reconnectingHandler?.(),
    triggerReconnected: () => reconnectedHandler?.(),
  };
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: function MockHubConnectionBuilder() {
    return signalRTestState.builder;
  },
  HubConnectionState: {
    Connected: 'Connected',
    Connecting: 'Connecting',
    Reconnecting: 'Reconnecting',
    Disconnecting: 'Disconnecting',
    Disconnected: 'Disconnected',
  },
  LogLevel: {
    Trace: 0,
    Debug: 1,
    Warning: 3,
    Error: 4,
    Critical: 5,
    None: 6,
    Information: 2,
  },
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getSettings: signalRTestState.getSettings,
    getQueueChanges: signalRTestState.getQueueChanges,
    getQueueChangeWatermark: signalRTestState.getQueueChangeWatermark,
  },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: vi.fn(() => 'http://localhost:5245/hubs/printers'),
  getSignalRAccessToken: vi.fn(() => localStorage.getItem('auth-token') || ''),
}));

describe('PrinterSignalRService auto-dispatch updates', () => {
  beforeEach(() => {
    // Re-evaluate the singleton after each mock setup; per-test isolation is intentional.
    vi.resetModules();
    vi.clearAllMocks();
    localStorage.clear();
    signalRTestState.connectionHandlers.clear();
    signalRTestState.connection.state = 'Disconnected';
    signalRTestState.connection.start.mockImplementation(async () => {
      signalRTestState.connection.state = 'Connected';
    });
    signalRTestState.connection.stop.mockImplementation(async () => {
      signalRTestState.connection.state = 'Disconnected';
    });
    signalRTestState.connection.invoke.mockResolvedValue(undefined);
    signalRTestState.getSettings.mockResolvedValue({
      logLevel: 'Information',
      consoleLoggingEnabled: false,
    });
    window.PrintFarmerDebug = undefined;
    localStorage.clear();
    signalRTestState.getQueueChanges.mockResolvedValue({
      afterSequence: 0,
      nextSequence: 0,
      hasMore: false,
      events: [],
    });
    signalRTestState.getQueueChangeWatermark.mockResolvedValue({
      latestSequence: 0,
    });
  });

  describe('PrinterSignalRService debug exposure gating', () => {
    const flushMicrotasks = async () => {
      for (let index = 0; index < 12; index++) {
        await Promise.resolve();
      }
    };

    beforeEach(() => {
      vi.resetModules();
      vi.clearAllMocks();
      signalRTestState.connectionHandlers.clear();
      signalRTestState.connection.state = 'Disconnected';
      signalRTestState.connection.start.mockImplementation(async () => {
        signalRTestState.connection.state = 'Connected';
      });
      signalRTestState.connection.stop.mockImplementation(async () => {
        signalRTestState.connection.state = 'Disconnected';
      });
      signalRTestState.connection.invoke.mockResolvedValue(undefined);
      signalRTestState.getSettings.mockResolvedValue({
        logLevel: 'Information',
        consoleLoggingEnabled: false,
      });
      signalRTestState.getQueueChanges.mockResolvedValue({
        afterSequence: 0,
        nextSequence: 0,
        hasMore: false,
        events: [],
      });
      signalRTestState.getQueueChangeWatermark.mockResolvedValue({
        latestSequence: 0,
      });
      localStorage.clear();
      window.PrintFarmerDebug = undefined;
    });

    it('does not populate window.PrintFarmerDebug.printerSignalR by default on printerupdated', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();

      signalRTestState.connectionHandlers.get('printerupdated')?.({
        id: 'printer-1',
        state: 'Printing',
        isOnline: true,
      });
      await flushMicrotasks();

      expect(window.PrintFarmerDebug?.printerSignalR).toBeUndefined();
      expect(window.PrintFarmerDebug?.lastPrinterUpdate).toBeUndefined();
      printerSignalRService.dispose();
    });

    it('does not console.debug on printerupdated by default (no self-enable)', async () => {
      const debugSpy = vi.spyOn(console, 'debug').mockImplementation(() => {});
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();

      // Fire multiple updates — the bug caused the first message to flip the
      // shared flag on, enabling logging from the second message onward.
      signalRTestState.connectionHandlers.get('printerupdated')?.({
        id: 'printer-1',
        state: 'Printing',
        isOnline: true,
      });
      signalRTestState.connectionHandlers.get('printerupdated')?.({
        id: 'printer-1',
        state: 'Idle',
        isOnline: true,
      });
      await flushMicrotasks();

      expect(debugSpy).not.toHaveBeenCalled();
      printerSignalRService.dispose();
      debugSpy.mockRestore();
    });

    it('populates window.PrintFarmerDebug.printerSignalR when printerSignalRVerbose is explicitly opted in', async () => {
      window.PrintFarmerDebug = { printerSignalRVerbose: true };
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();

      signalRTestState.connectionHandlers.get('printerupdated')?.({
        id: 'printer-1',
        state: 'Printing',
        isOnline: true,
      });
      await flushMicrotasks();

      expect(window.PrintFarmerDebug?.printerSignalR).toBeDefined();
      expect(
        (window.PrintFarmerDebug?.printerSignalR as { lastStatuses: Record<string, unknown> })
          .lastStatuses['printer-1']
      ).toBeDefined();
      printerSignalRService.dispose();
    });

    it('handles the batched "printerstatusesreplayed" event by applying each status', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();

      signalRTestState.connectionHandlers.get('printerstatusesreplayed')?.([
        { id: 'printer-1', state: 'Printing', isOnline: true },
        { id: 'printer-2', state: 'Idle', isOnline: true },
      ]);
      await flushMicrotasks();

      expect(printerSignalRService.getLastStatus('printer-1')?.state).toBe('Printing');
      expect(printerSignalRService.getLastStatus('printer-2')?.state).toBe('Idle');
      printerSignalRService.dispose();
    });

    it('still caches statuses (offline debounce logic) when the debug flag is off', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();

      signalRTestState.connectionHandlers.get('printerupdated')?.({
        id: 'printer-1',
        state: 'Printing',
        isOnline: true,
      });
      await flushMicrotasks();

      expect(printerSignalRService.getLastStatus('printer-1')).toMatchObject({
        id: 'printer-1',
        isOnline: true,
      });
      printerSignalRService.dispose();
    });
  });

  describe('PrinterSignalRService queue reconciliation', () => {
    const flushMicrotasks = async () => {
      for (let index = 0; index < 12; index++) {
        await Promise.resolve();
      }
    };

    beforeEach(() => {
      vi.resetModules();
      vi.clearAllMocks();
      signalRTestState.connectionHandlers.clear();
      signalRTestState.connection.state = 'Disconnected';
      signalRTestState.connection.start.mockImplementation(async () => {
        signalRTestState.connection.state = 'Connected';
      });
      signalRTestState.connection.stop.mockImplementation(async () => {
        signalRTestState.connection.state = 'Disconnected';
      });
      signalRTestState.connection.invoke.mockResolvedValue(undefined);
      signalRTestState.getSettings.mockResolvedValue({
        logLevel: 'Information',
        consoleLoggingEnabled: false,
      });
      signalRTestState.getQueueChanges.mockResolvedValue({
        afterSequence: 0,
        nextSequence: 0,
        hasMore: false,
        events: [],
      });
      signalRTestState.getQueueChangeWatermark.mockResolvedValue({
        latestSequence: 0,
      });
      localStorage.clear();
      window.PrintFarmerDebug = undefined;
    });

    it('authenticates the WebSocket from the current local bearer token', async () => {
      localStorage.setItem('auth-token', 'jwt-for-websocket');
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();

      const options = signalRTestState.builder.withUrl.mock.calls.at(-1)?.[1] as {
        accessTokenFactory: () => string;
      };
      expect(options.accessTokenFactory()).toBe('jwt-for-websocket');

      printerSignalRService.dispose();
    });

    it('proactively drains the durable cursor on the initial connection', async () => {
      signalRTestState.getQueueChanges.mockResolvedValueOnce({
        afterSequence: 0,
        nextSequence: 1,
        hasMore: false,
        events: [
          {
            schemaVersion: '3',
            eventId: 'event-1',
            sequence: 1,
            eventType: 'queue.updated',
            occurredAtUtc: '2026-07-28T00:00:00Z',
            calibrationAttemptId: 'calibration-attempt-1',
          },
        ],
      });
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      const callback = vi.fn();
      printerSignalRService.onQueueEvent(callback);

      await printerSignalRService.connect();

      expect(callback).toHaveBeenCalledWith(
        expect.objectContaining({
          sequence: 1,
          schemaVersion: '3',
          calibrationAttemptId: 'calibration-attempt-1',
        })
      );
      expect(printerSignalRService.getQueueSubscriptionSnapshot().lastSequence)
        .toBe(1);
      printerSignalRService.dispose();
    });

    it('advances the cursor and notifies subscribers instead of looping when the initial drain reports an expired cursor', async () => {
      signalRTestState.getQueueChanges.mockResolvedValueOnce({
        afterSequence: 0,
        nextSequence: 0,
        hasMore: false,
        events: [],
        expired: true,
        currentSequence: 500,
      });
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      const resourcesChangedCallback = vi.fn();
      printerSignalRService.onQueueResourcesChanged(resourcesChangedCallback);

      await printerSignalRService.connect();

      expect(signalRTestState.getQueueChanges).toHaveBeenCalledTimes(1);
      expect(resourcesChangedCallback).toHaveBeenCalledOnce();
      expect(printerSignalRService.getQueueSubscriptionSnapshot().lastSequence)
        .toBe(500);
      printerSignalRService.dispose();
    });

    it('advances the cursor and notifies subscribers instead of replaying when a sequence-gap fetch reports an expired cursor', async () => {
      signalRTestState.getQueueChanges.mockResolvedValueOnce({
        afterSequence: 0,
        nextSequence: 0,
        hasMore: false,
        events: [],
        expired: true,
        currentSequence: 99,
      });
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      const eventCallback = vi.fn();
      const resourcesChangedCallback = vi.fn();
      printerSignalRService.onQueueEvent(eventCallback);
      printerSignalRService.onQueueResourcesChanged(resourcesChangedCallback);

      // A live event arrives far ahead of the local cursor (0), forcing the
      // gap-fill path in handleQueueEvent to fetch missed events.
      signalRTestState.connectionHandlers.get('queueevent')?.({
        schemaVersion: '3',
        eventId: 'event-100',
        sequence: 100,
        eventType: 'queue.updated',
        occurredAtUtc: '2026-07-28T00:00:00Z',
      });
      await flushMicrotasks();

      // The gap-fill fetch reported the cursor as expired, so the live event
      // itself must not be delivered as if nothing were missed.
      expect(eventCallback).not.toHaveBeenCalled();
      expect(resourcesChangedCallback).toHaveBeenCalledOnce();
      expect(printerSignalRService.getQueueSubscriptionSnapshot().lastSequence)
        .toBe(99);
      printerSignalRService.dispose();
    });

    it('seeds the cursor from the server watermark on a fresh connect instead of replaying full outbox history', async () => {
      signalRTestState.getQueueChangeWatermark.mockResolvedValue({
        latestSequence: 750,
      });
      signalRTestState.getQueueChanges.mockResolvedValueOnce({
        afterSequence: 750,
        nextSequence: 750,
        hasMore: false,
        events: [],
      });
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      const callback = vi.fn();
      printerSignalRService.onQueueEvent(callback);

      await printerSignalRService.connect();

      expect(signalRTestState.getQueueChangeWatermark).toHaveBeenCalledTimes(1);
      expect(signalRTestState.getQueueChanges).toHaveBeenCalledWith(750);
      expect(signalRTestState.getQueueChanges).not.toHaveBeenCalledWith(0);
      expect(callback).not.toHaveBeenCalled();
      printerSignalRService.dispose();
    });

    it('still catches up a real gap on reconnect after the cursor has been seeded', async () => {
      signalRTestState.getQueueChangeWatermark.mockResolvedValue({
        latestSequence: 750,
      });
      signalRTestState.getQueueChanges
        .mockResolvedValueOnce({
          afterSequence: 750,
          nextSequence: 750,
          hasMore: false,
          events: [],
        })
        .mockResolvedValueOnce({
          afterSequence: 750,
          nextSequence: 752,
          hasMore: false,
          events: [
            {
              schemaVersion: '2',
              eventId: 'event-751',
              sequence: 751,
              eventType: 'queue.updated',
              occurredAtUtc: '2026-07-28T00:00:03Z',
            },
            {
              schemaVersion: '2',
              eventId: 'event-752',
              sequence: 752,
              eventType: 'queue.updated',
              occurredAtUtc: '2026-07-28T00:00:04Z',
            },
          ],
        });
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      const sequences: number[] = [];
      printerSignalRService.onQueueEvent((event) => sequences.push(event.sequence));

      await printerSignalRService.connect();
      expect(signalRTestState.getQueueChanges).toHaveBeenNthCalledWith(1, 750);

      // A genuine gap accumulates while disconnected; the reconnect must
      // still catch it up from the seeded (non-zero) cursor.
      signalRTestState.triggerReconnected();
      await flushMicrotasks();

      expect(sequences).toEqual([751, 752]);
      expect(signalRTestState.getQueueChanges).toHaveBeenNthCalledWith(2, 750);
      expect(signalRTestState.getQueueChangeWatermark).toHaveBeenCalledTimes(1);
      printerSignalRService.dispose();
    });

    it('is a no-op on reconnect with no gap after the cursor has been seeded', async () => {
      signalRTestState.getQueueChangeWatermark.mockResolvedValue({
        latestSequence: 750,
      });
      signalRTestState.getQueueChanges.mockResolvedValue({
        afterSequence: 750,
        nextSequence: 750,
        hasMore: false,
        events: [],
      });
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      const callback = vi.fn();
      printerSignalRService.onQueueEvent(callback);

      await printerSignalRService.connect();
      signalRTestState.getQueueChanges.mockClear();

      signalRTestState.triggerReconnected();
      await flushMicrotasks();

      expect(signalRTestState.getQueueChanges).toHaveBeenCalledWith(750);
      expect(callback).not.toHaveBeenCalled();
      expect(signalRTestState.getQueueChangeWatermark).toHaveBeenCalledTimes(1);
      printerSignalRService.dispose();
    });

    it('delivers payload-free queue resource discovery hints', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      const callback = vi.fn();
      printerSignalRService.onQueueResourcesChanged(callback);

      signalRTestState.connectionHandlers.get('queueresourceschanged')?.();

      expect(callback).toHaveBeenCalledOnce();
      printerSignalRService.dispose();
    });

    it('replays missed events before delivering a sequence-gap hint', async () => {
      signalRTestState.getQueueChanges.mockResolvedValueOnce({
        afterSequence: 0,
        nextSequence: 2,
        hasMore: false,
        events: [
          {
            schemaVersion: '2',
            eventId: 'event-1',
            sequence: 1,
            eventType: 'queue.updated',
            occurredAtUtc: '2026-07-28T00:00:00Z',
          },
          {
            schemaVersion: '2',
            eventId: 'event-2',
            sequence: 2,
            eventType: 'queue.updated',
            occurredAtUtc: '2026-07-28T00:00:01Z',
          },
        ],
      });
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      const sequences: number[] = [];
      printerSignalRService.onQueueEvent((event) => sequences.push(event.sequence));

      signalRTestState.connectionHandlers.get('queueevent')?.({
        schemaVersion: '2',
        eventId: 'event-3',
        sequence: 3,
        eventType: 'queue.updated',
        occurredAtUtc: '2026-07-28T00:00:02Z',
      });
      await flushMicrotasks();

      expect(sequences).toEqual([1, 2, 3]);
      printerSignalRService.dispose();
    });

    it('restores authorized groups and removes a rejected group on reconnect, with exactly one batched printer invocation', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      await printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: ['printer-1', 'printer-2', 'printer-3'],
        jobIds: ['job-1'],
        projectIds: ['project-1'],
      });
      signalRTestState.connection.invoke.mockClear();
      signalRTestState.connection.invoke.mockImplementation(
        async (method: string, arg: string | string[]) => {
          if (method === 'SubscribeToQueueJobAsync' && arg === 'job-1') {
            throw new Error('Forbidden');
          }
          if (method === 'SubscribeToPrintersAsync') {
            return arg as string[];
          }
        }
      );

      signalRTestState.triggerReconnected();
      await flushMicrotasks();

      // The whole point of issue #1764 is one batched invocation instead of N
      // serialized ones — assert the exact call count, not just call args, so
      // a regression back to N per-printer invokes would fail this test.
      const printerInvocations = signalRTestState.connection.invoke.mock.calls.filter(
        ([method]) => method === 'SubscribeToPrintersAsync'
      );
      expect(printerInvocations).toHaveLength(1);
      expect(printerInvocations[0][1]).toEqual(['printer-1', 'printer-2', 'printer-3']);
      expect(signalRTestState.connection.invoke).toHaveBeenCalledWith(
        'SubscribeToProjectAsync',
        'project-1'
      );
      expect(printerSignalRService.getQueueSubscriptionSnapshot()).toEqual(
        expect.objectContaining({
          printerIds: ['printer-1', 'printer-2', 'printer-3'],
          jobIds: [],
          projectIds: ['project-1'],
        })
      );
      printerSignalRService.dispose();
    });

    it('serializes an in-flight subscription behind the latest empty generation', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      const subscribeStarted = deferred<void>();
      const releaseSubscribe = deferred<void>();
      signalRTestState.connection.invoke.mockImplementation(
        async (method: string, id: string) => {
          if (method === 'SubscribeToPrinterAsync' && id === 'printer-a') {
            subscribeStarted.resolve();
            await releaseSubscribe.promise;
          }
        }
      );

      const staleApply =
        printerSignalRService.replaceQueueResourceSubscriptions({
          printerIds: ['printer-a'],
          jobIds: [],
          projectIds: [],
        });
      await subscribeStarted.promise;
      const cleanup = printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: [],
        jobIds: [],
        projectIds: [],
      });
      releaseSubscribe.resolve();
      await Promise.all([staleApply, cleanup]);

      expect(printerSignalRService.getQueueSubscriptionSnapshot()).toEqual(
        expect.objectContaining({
          printerIds: [],
          jobIds: [],
          projectIds: [],
        })
      );
      expect(signalRTestState.connection.invoke).toHaveBeenCalledWith(
        'UnsubscribeFromPrinterAsync',
        'printer-a'
      );
      printerSignalRService.dispose();
    });

    it('subscribeToPrinters issues a single batched invocation for multiple printers', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      signalRTestState.connection.invoke.mockClear();
      signalRTestState.connection.invoke.mockImplementation(
        async (method: string, ids: string[]) => {
          if (method === 'SubscribeToPrintersAsync') {
            return ids;
          }
        }
      );

      await printerSignalRService.subscribeToPrinters([
        'printer-a',
        'printer-b',
        'printer-c',
      ]);

      const printerInvocations = signalRTestState.connection.invoke.mock.calls.filter(
        ([method]) => method === 'SubscribeToPrintersAsync'
      );
      expect(printerInvocations).toHaveLength(1);
      expect(printerInvocations[0][1]).toEqual(
        expect.arrayContaining(['printer-a', 'printer-b', 'printer-c'])
      );
      printerSignalRService.dispose();
    });

    it('subscribeToPrinters drops printers the server did not authorize, and does not retry them on reconnect', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      signalRTestState.connection.invoke.mockImplementation(
        async (method: string, ids: string[]) => {
          if (method === 'SubscribeToPrintersAsync') {
            return ids.filter((id) => id !== 'printer-forbidden');
          }
        }
      );

      await printerSignalRService.subscribeToPrinters([
        'printer-allowed',
        'printer-forbidden',
      ]);

      const snapshot = printerSignalRService.getQueueSubscriptionSnapshot();
      expect(snapshot.printerIds).toEqual(['printer-allowed']);

      // The unauthorized id must be dropped from the desired set too, not just
      // from `subscribedPrinters` — otherwise a later reconnect would keep
      // re-requesting it forever.
      signalRTestState.connection.invoke.mockClear();
      signalRTestState.connection.invoke.mockImplementation(
        async (method: string, ids: string[]) => {
          if (method === 'SubscribeToPrintersAsync') {
            return ids;
          }
        }
      );
      signalRTestState.triggerReconnected();
      await flushMicrotasks();

      const printerInvocations = signalRTestState.connection.invoke.mock.calls.filter(
        ([method]) => method === 'SubscribeToPrintersAsync'
      );
      expect(printerInvocations).toHaveLength(1);
      expect(printerInvocations[0][1]).toEqual(['printer-allowed']);
      printerSignalRService.dispose();
    });

    it('subscribeToPrinters drops all requested printers when the batched invocation fails, and does not retry them on reconnect', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      signalRTestState.connection.invoke.mockImplementation(
        async (method: string) => {
          if (method === 'SubscribeToPrintersAsync') {
            throw new Error('network error');
          }
        }
      );

      await printerSignalRService.subscribeToPrinters(['printer-a', 'printer-b']);

      const snapshot = printerSignalRService.getQueueSubscriptionSnapshot();
      expect(snapshot.printerIds).toEqual([]);

      // A subsequent reconnect must not re-request the ids the failed batch
      // invocation dropped from the desired set.
      signalRTestState.connection.invoke.mockClear();
      signalRTestState.connection.invoke.mockResolvedValue(undefined);
      signalRTestState.triggerReconnected();
      await flushMicrotasks();

      const printerInvocations = signalRTestState.connection.invoke.mock.calls.filter(
        ([method]) => method === 'SubscribeToPrintersAsync'
      );
      expect(printerInvocations).toHaveLength(0);
      printerSignalRService.dispose();
    });

    it('logout clears desired ownership so reconnect restores no stale groups', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      await printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: ['printer-logout'],
        jobIds: ['job-logout'],
        projectIds: ['project-logout'],
      });
      signalRTestState.connection.invoke.mockClear();

      const releaseGeneration =
        await printerSignalRService.replaceQueueResourceSubscriptions({
          printerIds: [],
          jobIds: [],
          projectIds: [],
        });
      await printerSignalRService.disconnect(releaseGeneration);

      expect(printerSignalRService.getQueueSubscriptionSnapshot()).toEqual(
        expect.objectContaining({
          printerIds: [],
          jobIds: [],
          projectIds: [],
        })
      );
      signalRTestState.connection.invoke.mockClear();
      signalRTestState.connection.state = 'Connected';
      signalRTestState.triggerReconnected();
      await flushMicrotasks();
      expect(signalRTestState.connection.invoke).not.toHaveBeenCalledWith(
        expect.stringMatching(/^SubscribeTo/),
        expect.anything()
      );
      printerSignalRService.dispose();
    });

    it('reconnect waits for an in-flight generation and observes newer cleanup', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      const subscribeStarted = deferred<void>();
      const releaseSubscribe = deferred<void>();
      signalRTestState.connection.invoke.mockImplementation(
        async (method: string, id: string) => {
          if (method === 'SubscribeToQueueJobAsync' && id === 'job-stale') {
            subscribeStarted.resolve();
            await releaseSubscribe.promise;
          }
        }
      );

      const staleApply =
        printerSignalRService.replaceQueueResourceSubscriptions({
          printerIds: [],
          jobIds: ['job-stale'],
          projectIds: [],
        });
      await subscribeStarted.promise;
      const cleanup = printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: [],
        jobIds: [],
        projectIds: [],
      });
      signalRTestState.triggerReconnected();
      releaseSubscribe.resolve();
      await Promise.all([staleApply, cleanup]);
      await flushMicrotasks();
      signalRTestState.connection.invoke.mockClear();

      signalRTestState.triggerReconnected();
      await flushMicrotasks();

      expect(printerSignalRService.getQueueSubscriptionSnapshot().jobIds).toEqual(
        []
      );
      expect(signalRTestState.connection.invoke).not.toHaveBeenCalledWith(
        'SubscribeToQueueJobAsync',
        'job-stale'
      );
      printerSignalRService.dispose();
    });

    it('retains a failed unsubscribe in applied state and retries it', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      await printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: ['printer-retry'],
        jobIds: [],
        projectIds: [],
      });
      let unsubscribeAttempts = 0;
      signalRTestState.connection.invoke.mockImplementation(
        async (method: string, id: string) => {
          if (
            method === 'UnsubscribeFromPrinterAsync' &&
            id === 'printer-retry'
          ) {
            unsubscribeAttempts++;
            if (unsubscribeAttempts === 1) {
              throw new Error('transient unsubscribe failure');
            }
          }
        }
      );

      await expect(
        printerSignalRService.replaceQueueResourceSubscriptions({
          printerIds: [],
          jobIds: [],
          projectIds: [],
        })
      ).rejects.toThrow('transient unsubscribe failure');
      expect(
        printerSignalRService.getQueueSubscriptionSnapshot().printerIds
      ).toEqual(['printer-retry']);

      await printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: [],
        jobIds: [],
        projectIds: [],
      });

      expect(unsubscribeAttempts).toBe(2);
      expect(
        printerSignalRService.getQueueSubscriptionSnapshot().printerIds
      ).toEqual([]);
      printerSignalRService.dispose();
    });

    it('hydrates applied state when reconnect restores a transiently failed group', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      signalRTestState.connection.invoke.mockRejectedValueOnce(
        new Error('transient subscribe failure')
      );

      await expect(
        printerSignalRService.subscribeToQueueJob('job-recover')
      ).rejects.toThrow('transient subscribe failure');
      expect(
        printerSignalRService.getQueueSubscriptionSnapshot().jobIds
      ).toEqual([]);

      signalRTestState.connection.invoke.mockResolvedValue(undefined);
      signalRTestState.triggerReconnected();
      await flushMicrotasks();

      expect(signalRTestState.connection.invoke).toHaveBeenCalledWith(
        'SubscribeToQueueJobAsync',
        'job-recover'
      );
      expect(
        printerSignalRService.getQueueSubscriptionSnapshot().jobIds
      ).toEqual(['job-recover']);
      printerSignalRService.dispose();
    });

    it('aborts Connecting and fences a late authenticated start completion', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      const startEntered = deferred<void>();
      const releaseStart = deferred<void>();
      signalRTestState.connection.start.mockImplementation(async () => {
        signalRTestState.connection.state = 'Connecting';
        startEntered.resolve();
        await releaseStart.promise;
        signalRTestState.connection.state = 'Connected';
      });
      const connected = vi.fn();
      printerSignalRService.onConnectionStateChange(connected);

      const connect = printerSignalRService.connect();
      await startEntered.promise;
      const teardown =
        printerSignalRService.releaseQueueResourceSubscriptionsAndDisconnect();
      releaseStart.resolve();
      await Promise.all([connect, teardown]);

      expect(signalRTestState.connection.stop).toHaveBeenCalled();
      expect(signalRTestState.connection.state).toBe('Disconnected');
      expect(signalRTestState.getQueueChanges).not.toHaveBeenCalled();
      expect(connected).not.toHaveBeenCalledWith(true);
      printerSignalRService.dispose();
    });

    it('stops Reconnecting and rejects a late automatic reconnect callback', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      await printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: [],
        jobIds: ['job-reconnecting'],
        projectIds: [],
      });
      signalRTestState.connection.invoke.mockClear();
      signalRTestState.connection.state = 'Reconnecting';
      signalRTestState.triggerReconnecting();

      await printerSignalRService.releaseQueueResourceSubscriptionsAndDisconnect();
      signalRTestState.connection.state = 'Connected';
      signalRTestState.triggerReconnected();
      await flushMicrotasks();

      expect(signalRTestState.connection.stop).toHaveBeenCalled();
      expect(signalRTestState.connection.invoke).not.toHaveBeenCalledWith(
        'SubscribeToQueueJobAsync',
        'job-reconnecting'
      );
      printerSignalRService.dispose();
    });

    it('cancels a tracked manual reconnect retry during teardown', async () => {
      vi.useFakeTimers();
      try {
        const { printerSignalRService } = await import('../printer-signalr');
        await flushMicrotasks();
        signalRTestState.connection.start.mockImplementation(async () => {
          signalRTestState.connection.state = 'Disconnected';
          throw new Error('initial start failed');
        });

        await printerSignalRService.connect();
        expect(signalRTestState.connection.start).toHaveBeenCalledOnce();
        await printerSignalRService.disconnect();
        await vi.advanceTimersByTimeAsync(60_000);

        expect(signalRTestState.connection.start).toHaveBeenCalledOnce();
        printerSignalRService.dispose();
      } finally {
        vi.useRealTimers();
      }
    });

    it('journals a deferred restore join so newer cleanup unsubscribes it', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      await printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: [],
        jobIds: ['job-deferred'],
        projectIds: [],
      });
      signalRTestState.connection.state = 'Reconnecting';
      signalRTestState.triggerReconnecting();
      const restoreStarted = deferred<void>();
      const releaseRestore = deferred<void>();
      signalRTestState.connection.invoke.mockImplementation(
        async (method: string, id: string) => {
          if (
            method === 'SubscribeToQueueJobAsync' &&
            id === 'job-deferred'
          ) {
            restoreStarted.resolve();
            await releaseRestore.promise;
          }
        }
      );
      signalRTestState.connection.state = 'Connected';
      signalRTestState.triggerReconnected();
      await restoreStarted.promise;

      const cleanup = printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: [],
        jobIds: [],
        projectIds: [],
      });
      releaseRestore.resolve();
      await cleanup;

      expect(signalRTestState.connection.invoke).toHaveBeenCalledWith(
        'UnsubscribeFromQueueJobAsync',
        'job-deferred'
      );
      expect(
        printerSignalRService.getQueueSubscriptionSnapshot().jobIds
      ).toEqual([]);
      printerSignalRService.dispose();
    });

    it('does not journal a deferred restore from an obsolete connection epoch', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      await printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: [],
        jobIds: ['job-epoch'],
        projectIds: [],
      });
      signalRTestState.connection.state = 'Reconnecting';
      signalRTestState.triggerReconnecting();
      const firstRestoreStarted = deferred<void>();
      const releaseFirstRestore = deferred<void>();
      let subscribeCalls = 0;
      signalRTestState.connection.invoke.mockImplementation(
        async (method: string, id: string) => {
          if (method === 'SubscribeToQueueJobAsync' && id === 'job-epoch') {
            subscribeCalls++;
            if (subscribeCalls === 1) {
              firstRestoreStarted.resolve();
              await releaseFirstRestore.promise;
            }
          }
        }
      );
      signalRTestState.connection.state = 'Connected';
      signalRTestState.triggerReconnected();
      await firstRestoreStarted.promise;

      signalRTestState.connection.state = 'Reconnecting';
      signalRTestState.triggerReconnecting();
      signalRTestState.connection.state = 'Connected';
      signalRTestState.triggerReconnected();
      releaseFirstRestore.resolve();
      await flushMicrotasks();

      expect(subscribeCalls).toBe(2);
      expect(
        printerSignalRService.getQueueSubscriptionSnapshot().jobIds
      ).toEqual(['job-epoch']);
      await printerSignalRService.releaseQueueResourceSubscriptionsAndDisconnect();
      expect(signalRTestState.connection.stop).toHaveBeenCalled();
      expect(
        printerSignalRService.getQueueSubscriptionSnapshot().jobIds
      ).toEqual([]);
      printerSignalRService.dispose();
    });
  });

  it('uses the canonical auth token for the secured printer hub', async () => {
    localStorage.setItem('auth-token', 'jwt-printer');

    const { printerSignalRService } = await import('../printer-signalr');
    await vi.waitFor(() => expect(signalRTestState.builder.withUrl).toHaveBeenCalled());

    const options = signalRTestState.builder.withUrl.mock.calls[0][1] as {
      accessTokenFactory: () => string;
    };
    expect(options.accessTokenFactory()).toBe('jwt-printer');

    printerSignalRService.dispose();
  });

  it('registers the auto-dispatch event name for status updates', async () => {
    const { printerSignalRService } = await import('../printer-signalr');

    await Promise.resolve();
    await Promise.resolve();

    expect(signalRTestState.connection.on).toHaveBeenCalledWith(
      'autodispatchstatechanged',
      expect.any(Function),
    );

    printerSignalRService.dispose();
  });

  it('delivers auto-dispatch payloads to subscribers', async () => {
    const { printerSignalRService } = await import('../printer-signalr');

    await Promise.resolve();
    await Promise.resolve();

    const nextStatus: AutoDispatchStatus = {
      printerId: 'printer-1',
      enabled: true,
      state: 'PendingReady',
      queueDepth: 2,
    };

    const onAutoDispatchStateChanged = vi.fn();
    const unsubscribeDispatch = printerSignalRService.onAutoDispatchStateChanged(onAutoDispatchStateChanged);

    const handler = signalRTestState.connectionHandlers.get('autodispatchstatechanged');

    expect(handler).toBeDefined();

    handler?.(nextStatus);

    expect(onAutoDispatchStateChanged).toHaveBeenCalledWith(nextStatus);

    unsubscribeDispatch();
    printerSignalRService.dispose();
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Regression coverage for #1590: the SignalR settings loader used to run
// unconditionally at module-import time, before the user authenticates. Against the
// hardened UnifiedSettingsController that anonymous GET /api/settings/SignalR failed
// closed (401), producing a doomed request and a console warning on every signed-out
// page (including /login) before this fix. The constructor must now skip the network
// call entirely when no session exists yet, falling straight back to defaults, and
// only fetch real settings once a session is established (or immediately, on a page
// refresh while a session already exists).
// ─────────────────────────────────────────────────────────────────────────────
describe('PrinterSignalRService settings reload on authentication', () => {
  // Must stay in sync with AUTH_SESSION_ESTABLISHED_EVENT in src/services/authEvents.ts.
  const AUTH_EVENT = 'printfarmer:auth-session-established';

  const flushMicrotasks = async () => {
    for (let i = 0; i < 6; i++) {
      await Promise.resolve();
    }
  };

  const createDeferredSettings = () => {
    let resolve!: (value: { logLevel: string; consoleLoggingEnabled: boolean }) => void;
    const promise = new Promise<{ logLevel: string; consoleLoggingEnabled: boolean }>((resolvePromise) => {
      resolve = resolvePromise;
    });
    return { promise, resolve };
  };

  beforeEach(() => {
    vi.resetModules();
    vi.clearAllMocks();
    signalRTestState.getSettings.mockReset();
    signalRTestState.connectionHandlers.clear();
    signalRTestState.connection.state = 'Disconnected';
    signalRTestState.connection.start.mockImplementation(async () => {
      signalRTestState.connection.state = 'Connected';
    });
    signalRTestState.connection.stop.mockImplementation(async () => {
      signalRTestState.connection.state = 'Disconnected';
    });
    signalRTestState.getQueueChangeWatermark.mockResolvedValue({
      latestSequence: 0,
    });
    window.PrintFarmerDebug = undefined;
    localStorage.clear();
  });

  it('never calls the protected settings endpoint when no session exists yet', async () => {
    const consoleWarn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);

    const { printerSignalRService } = await import('../printer-signalr');
    await flushMicrotasks();

    // No stored auth token: the constructor must not fire the anonymous,
    // protected GET /api/settings/SignalR at all (this is the #1590 fix), and it
    // still builds a working connection using the same defaults loadSettings()
    // falls back to on failure.
    expect(signalRTestState.getSettings).not.toHaveBeenCalled();
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);
    expect(consoleWarn).not.toHaveBeenCalled();

    consoleWarn.mockRestore();
    printerSignalRService.dispose();
  });

  it('fetches settings immediately when a session already exists at construction', async () => {
    localStorage.setItem('auth-token', 'existing-token');
    signalRTestState.getSettings.mockResolvedValue({ logLevel: 'Information', consoleLoggingEnabled: false });

    const { printerSignalRService } = await import('../printer-signalr');
    await flushMicrotasks();

    // A page refresh while already signed in must still load real settings
    // up front, since a session is genuinely available.
    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(1);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);

    printerSignalRService.dispose();
  });

  it('reloads settings and rebuilds the connection when the log level changes after auth', async () => {
    // No session at construction: the anonymous call is skipped and defaults are
    // used (logLevel Information, consoleLoggingEnabled true). The post-auth load
    // returns the admin config, here with console logging disabled so the
    // effective log level changes.
    signalRTestState.getSettings.mockResolvedValue({ logLevel: 'Information', consoleLoggingEnabled: false });

    const { printerSignalRService } = await import('../printer-signalr');
    await flushMicrotasks();

    // Baseline: no network call yet, and the connection was built once with defaults.
    expect(signalRTestState.getSettings).not.toHaveBeenCalled();
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);

    // Simulate the user authenticating.
    window.dispatchEvent(new Event(AUTH_EVENT));
    await flushMicrotasks();

    // The service must load settings and rebuild the connection so the admin's
    // configured log level actually takes effect.
    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(1);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(2);

    printerSignalRService.dispose();
  });

  it('reloads settings but does not rebuild when the effective log level is unchanged', async () => {
    // Post-auth config resolves to the same effective level as the defaults used
    // before authentication, so there is nothing to rebuild — avoids needless
    // reconnect churn on login.
    signalRTestState.getSettings.mockResolvedValue({ logLevel: 'Information', consoleLoggingEnabled: true });

    const { printerSignalRService } = await import('../printer-signalr');
    await flushMicrotasks();

    expect(signalRTestState.getSettings).not.toHaveBeenCalled();
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);

    window.dispatchEvent(new Event(AUTH_EVENT));
    await flushMicrotasks();

    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(1);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);

    printerSignalRService.dispose();
  });

  it('does not let a late initial settings response overwrite authenticated settings', async () => {
    // A session already exists at construction, so the initial (slow) load fires
    // for real; a second, faster load triggered by re-authentication must win even
    // though the first settles later.
    localStorage.setItem('auth-token', 'existing-token');
    const initialSettings = createDeferredSettings();
    const authenticatedSettings = createDeferredSettings();
    signalRTestState.getSettings
      .mockReturnValueOnce(initialSettings.promise)
      .mockReturnValueOnce(authenticatedSettings.promise)
      .mockResolvedValue({ logLevel: 'Information', consoleLoggingEnabled: false });

    const { printerSignalRService } = await import('../printer-signalr');

    window.dispatchEvent(new Event(AUTH_EVENT));
    await vi.waitFor(() => expect(signalRTestState.getSettings).toHaveBeenCalledTimes(2));

    authenticatedSettings.resolve({ logLevel: 'Information', consoleLoggingEnabled: false });
    await flushMicrotasks();
    initialSettings.resolve({ logLevel: 'Information', consoleLoggingEnabled: true });
    await flushMicrotasks();

    window.dispatchEvent(new Event(AUTH_EVENT));
    await flushMicrotasks();

    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(3);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);

    printerSignalRService.dispose();
  });

  it('queues another authenticated refresh when one is already active', async () => {
    const firstAuthenticatedSettings = createDeferredSettings();
    const secondAuthenticatedSettings = createDeferredSettings();
    signalRTestState.getSettings
      .mockReturnValueOnce(firstAuthenticatedSettings.promise)
      .mockReturnValueOnce(secondAuthenticatedSettings.promise);

    const { printerSignalRService } = await import('../printer-signalr');
    await flushMicrotasks();

    // No session yet: the constructor skipped the network call and built once
    // using defaults.
    expect(signalRTestState.getSettings).not.toHaveBeenCalled();
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);

    window.dispatchEvent(new Event(AUTH_EVENT));
    await vi.waitFor(() => expect(signalRTestState.getSettings).toHaveBeenCalledTimes(1));
    window.dispatchEvent(new Event(AUTH_EVENT));

    firstAuthenticatedSettings.resolve({ logLevel: 'Warning', consoleLoggingEnabled: true });
    await vi.waitFor(() => expect(signalRTestState.getSettings).toHaveBeenCalledTimes(2));
    secondAuthenticatedSettings.resolve({ logLevel: 'Critical', consoleLoggingEnabled: true });
    await flushMicrotasks();

    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(3);

    printerSignalRService.dispose();
  });

  it('filters custom logger messages below the configured threshold', async () => {
    // A session already exists at construction so the initial load happens for
    // real, matching the "signed in, then refresh" scenario this test exercises.
    localStorage.setItem('auth-token', 'existing-token');
    signalRTestState.getSettings
      .mockResolvedValueOnce({
        logLevel: 'Warning',
        consoleLoggingEnabled: true,
      })
      .mockResolvedValue({
        logLevel: 'Critical',
        consoleLoggingEnabled: false,
      });
    window.PrintFarmerDebug = { printerSignalR: true };
    const consoleLog = vi.spyOn(console, 'log').mockImplementation(() => undefined);

    const { printerSignalRService } = await import('../printer-signalr');
    await flushMicrotasks();

    const logger = signalRTestState.builder.configureLogging.mock.calls[0][0] as {
      log: (logLevel: number, message: string) => void;
    };
    logger.log(2, 'information message');
    logger.log(3, 'warning message');

    expect(consoleLog).toHaveBeenCalledTimes(1);
    expect(consoleLog).toHaveBeenCalledWith('[SignalR Warning] warning message');

    window.dispatchEvent(new Event(AUTH_EVENT));
    await flushMicrotasks();
    const disabledLogger = signalRTestState.builder.configureLogging.mock.calls[1][0] as {
      log: (logLevel: number, message: string) => void;
    };
    disabledLogger.log(5, 'critical message');

    expect(consoleLog).toHaveBeenCalledTimes(1);

    consoleLog.mockRestore();

    printerSignalRService.dispose();
  });

  it('does not reconnect when logout supersedes a deferred settings stop', async () => {
    // A session exists at construction so the initial settings load fires for
    // real, matching the pre-existing session scenario this test exercises.
    localStorage.setItem('auth-token', 'existing-token');
    signalRTestState.getSettings.mockResolvedValueOnce({
      logLevel: 'Information',
      consoleLoggingEnabled: false,
    });
    const { printerSignalRService } = await import('../printer-signalr');
    await flushMicrotasks();
    await printerSignalRService.connect();
    expect(signalRTestState.connection.start).toHaveBeenCalledOnce();

    signalRTestState.getSettings.mockResolvedValueOnce({
      logLevel: 'Information',
      consoleLoggingEnabled: true,
    });
    const stopEntered = deferred<void>();
    const releaseStop = deferred<void>();
    signalRTestState.connection.stop.mockImplementation(async () => {
      signalRTestState.connection.state = 'Disconnecting';
      stopEntered.resolve();
      await releaseStop.promise;
      signalRTestState.connection.state = 'Disconnected';
    });

    const refresh = printerSignalRService.refreshSettings();
    await stopEntered.promise;
    const logout = printerSignalRService.disconnect();
    releaseStop.resolve();
    await Promise.all([refresh, logout]);

    expect(signalRTestState.connection.start).toHaveBeenCalledOnce();
    expect(printerSignalRService.isConnected).toBe(false);
    printerSignalRService.dispose();
  });
});
