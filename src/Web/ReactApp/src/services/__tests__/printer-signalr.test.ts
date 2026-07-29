import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AutoDispatchStatus } from '@/types/api';

const signalRTestState = vi.hoisted(() => {
  const connectionHandlers = new Map<string, (...args: unknown[]) => void>();
  const connection = {
    on: vi.fn((eventName: string, callback: (...args: unknown[]) => void) => {
      connectionHandlers.set(eventName, callback);
    }),
    onclose: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
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
    signalRTestState.getSettings.mockResolvedValue({
      logLevel: 'Information',
      consoleLoggingEnabled: false,
    });
    window.PrintFarmerDebug = undefined;
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
});
