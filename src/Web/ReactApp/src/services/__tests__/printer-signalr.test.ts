import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AutoDispatchStatus } from '@/types/api';

const signalRTestState = vi.hoisted(() => {
  const connectionHandlers = new Map<string, (...args: unknown[]) => void>();
  let reconnectedHandler: (() => void) | undefined;
  const connection = {
    on: vi.fn((eventName: string, callback: (...args: unknown[]) => void) => {
      connectionHandlers.set(eventName, callback);
    }),
    onclose: vi.fn(),
    onreconnecting: vi.fn(),
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
    Disconnected: 'Disconnected',
  },
  LogLevel: {
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
}));

describe('PrinterSignalRService auto-dispatch updates', () => {
  beforeEach(() => {
    vi.resetModules();
    vi.clearAllMocks();
    signalRTestState.connectionHandlers.clear();
    signalRTestState.connection.state = 'Disconnected';
    signalRTestState.connection.start.mockImplementation(async () => {
      signalRTestState.connection.state = 'Connected';
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
            schemaVersion: '2',
            eventId: 'event-1',
            sequence: 1,
            eventType: 'queue.updated',
            occurredAtUtc: '2026-07-28T00:00:00Z',
          },
        ],
      });
      const { printerSignalRService } = await import('../printer-signalr');
      await flushMicrotasks();
      const callback = vi.fn();
      printerSignalRService.onQueueEvent(callback);

      await printerSignalRService.connect();

      expect(callback).toHaveBeenCalledWith(
        expect.objectContaining({ sequence: 1 })
      );
      expect(printerSignalRService.getQueueSubscriptionSnapshot().lastSequence)
        .toBe(1);
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
      signalRTestState.connection.state = 'Connected';
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

  beforeEach(() => {
    vi.resetModules();
    vi.clearAllMocks();
    signalRTestState.getSettings.mockReset();
    signalRTestState.connectionHandlers.clear();
    signalRTestState.connection.state = 'Disconnected';
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
});
