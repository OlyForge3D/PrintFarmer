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
}));

describe('PrinterSignalRService auto-dispatch updates', () => {
  beforeEach(() => {
    vi.resetModules();
    vi.clearAllMocks();
    signalRTestState.connectionHandlers.clear();
    signalRTestState.connection.state = 'Disconnected';
    signalRTestState.getSettings.mockResolvedValue({
      logLevel: 'Information',
      consoleLoggingEnabled: false,
    });
    window.PrintFarmerDebug = undefined;
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
