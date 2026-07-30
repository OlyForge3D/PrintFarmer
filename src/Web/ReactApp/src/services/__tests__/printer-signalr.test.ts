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
  },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: vi.fn(() => 'http://localhost:5245/hubs/printers'),
  getSignalRAccessToken: vi.fn(() => localStorage.getItem('auth-token') || ''),
}));

describe('PrinterSignalRService auto-dispatch updates', () => {
  beforeEach(() => {
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

    it('restores authorized groups and removes a rejected group on reconnect', async () => {
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      await printerSignalRService.connect();
      await printerSignalRService.replaceQueueResourceSubscriptions({
        printerIds: ['printer-1'],
        jobIds: ['job-1'],
        projectIds: ['project-1'],
      });
      signalRTestState.connection.invoke.mockClear();
      signalRTestState.connection.invoke.mockImplementation(
        async (method: string, id: string) => {
          if (method === 'SubscribeToQueueJobAsync' && id === 'job-1') {
            throw new Error('Forbidden');
          }
        }
      );

      signalRTestState.triggerReconnected();
      await flushMicrotasks();

      expect(signalRTestState.connection.invoke).toHaveBeenCalledWith(
        'SubscribeToPrinterAsync',
        'printer-1'
      );
      expect(signalRTestState.connection.invoke).toHaveBeenCalledWith(
        'SubscribeToProjectAsync',
        'project-1'
      );
      expect(printerSignalRService.getQueueSubscriptionSnapshot()).toEqual(
        expect.objectContaining({
          printerIds: ['printer-1'],
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
// Regression coverage for the #950 follow-up: the SignalR settings loader runs
// once, at module-import time, before the user authenticates. Against the
// hardened UnifiedSettingsController the anonymous GET /api/settings/SignalR
// fails closed (401), so the service silently falls back to defaults. Before this
// fix loadSettings() was never re-run, so the admin-configured log level was
// ignored for the entire session until a manual page refresh. The service must
// reload its settings once a session is established.
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
    window.PrintFarmerDebug = undefined;
  });

  it('reloads settings and rebuilds the connection when the log level changes after auth', async () => {
    // Pre-auth load fails closed (401); post-auth load returns the admin config,
    // here with console logging disabled so the effective log level changes.
    signalRTestState.getSettings
      .mockRejectedValueOnce(new Error('Unauthorized'))
      .mockResolvedValue({ logLevel: 'Information', consoleLoggingEnabled: false });

    const { printerSignalRService } = await import('../printer-signalr');
    await flushMicrotasks();

    // Baseline: the constructor loaded once (falling back to defaults) and built
    // the connection once with the default log level.
    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(1);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);

    // Simulate the user authenticating.
    window.dispatchEvent(new Event(AUTH_EVENT));
    await flushMicrotasks();

    // The service must reload settings and rebuild the connection so the admin's
    // configured log level actually takes effect.
    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(2);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(2);

    printerSignalRService.dispose();
  });

  it('reloads settings but does not rebuild when the effective log level is unchanged', async () => {
    // Pre-auth defaults and post-auth config resolve to the same effective level,
    // so there is nothing to rebuild — avoids needless reconnect churn on login.
    signalRTestState.getSettings
      .mockRejectedValueOnce(new Error('Unauthorized'))
      .mockResolvedValue({ logLevel: 'Information', consoleLoggingEnabled: true });

    const { printerSignalRService } = await import('../printer-signalr');
    await flushMicrotasks();

    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(1);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);

    window.dispatchEvent(new Event(AUTH_EVENT));
    await flushMicrotasks();

    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(2);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);

    printerSignalRService.dispose();
  });

  it('does not let a late initial settings response overwrite authenticated settings', async () => {
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
      .mockResolvedValueOnce({ logLevel: 'Information', consoleLoggingEnabled: true })
      .mockReturnValueOnce(firstAuthenticatedSettings.promise)
      .mockReturnValueOnce(secondAuthenticatedSettings.promise);

    const { printerSignalRService } = await import('../printer-signalr');
    await flushMicrotasks();

    window.dispatchEvent(new Event(AUTH_EVENT));
    await vi.waitFor(() => expect(signalRTestState.getSettings).toHaveBeenCalledTimes(2));
    window.dispatchEvent(new Event(AUTH_EVENT));

    firstAuthenticatedSettings.resolve({ logLevel: 'Warning', consoleLoggingEnabled: true });
    await vi.waitFor(() => expect(signalRTestState.getSettings).toHaveBeenCalledTimes(3));
    secondAuthenticatedSettings.resolve({ logLevel: 'Critical', consoleLoggingEnabled: true });
    await flushMicrotasks();

    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(3);

    printerSignalRService.dispose();
  });

  it('filters custom logger messages below the configured threshold', async () => {
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
