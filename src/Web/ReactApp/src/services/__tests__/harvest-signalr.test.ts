import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// The global test setup (src/test/setup.ts) replaces this module with a stub so
// most tests don't spin up a real connection. This suite exercises the REAL
// service, so opt out of that global mock for this file only.
vi.unmock('@/services/harvest-signalr');

const signalRTestState = vi.hoisted(() => {
  const connection = {
    on: vi.fn(),
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
  getHubUrl: vi.fn(() => 'http://localhost:5245/hubs/harvest'),
  getSignalRAccessToken: vi.fn(() => ''),
}));

// ─────────────────────────────────────────────────────────────────────────────
// Twin of the printer-signalr regression: the harvest SignalR service also loads
// its settings once, at module-import time, before the user authenticates. The
// hardened UnifiedSettingsController fails the anonymous GET /api/settings/SignalR
// closed (401), so the service falls back to defaults. It must reload its settings
// once a session is established, otherwise the admin log level is ignored for the
// whole session.
// ─────────────────────────────────────────────────────────────────────────────
describe('SignalRService (harvest) settings reload on authentication', () => {
  // Must stay in sync with AUTH_SESSION_ESTABLISHED_EVENT in src/services/authEvents.ts.
  const AUTH_EVENT = 'printfarmer:auth-session-established';

  // Track the created singleton so it is always torn down — even if a test fails
  // mid-way — so its window auth listener can't leak into the next test.
  let service: { dispose: () => void } | null = null;

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
    signalRTestState.connection.state = 'Disconnected';
    (window as unknown as { PrintFarmerDebug?: unknown }).PrintFarmerDebug = undefined;
  });

  afterEach(() => {
    service?.dispose();
    service = null;
  });

  it('reloads settings and rebuilds the connection when the log level changes after auth', async () => {
    signalRTestState.getSettings
      .mockRejectedValueOnce(new Error('Unauthorized'))
      .mockResolvedValue({ logLevel: 'Information', consoleLoggingEnabled: false });

    const { signalRService } = await import('../harvest-signalr');
    service = signalRService;
    await flushMicrotasks();

    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(1);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);

    window.dispatchEvent(new Event(AUTH_EVENT));
    await flushMicrotasks();

    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(2);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(2);
  });

  it('reloads settings but does not rebuild when the effective log level is unchanged', async () => {
    signalRTestState.getSettings
      .mockRejectedValueOnce(new Error('Unauthorized'))
      .mockResolvedValue({ logLevel: 'Information', consoleLoggingEnabled: true });

    const { signalRService } = await import('../harvest-signalr');
    service = signalRService;
    await flushMicrotasks();

    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(1);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);

    window.dispatchEvent(new Event(AUTH_EVENT));
    await flushMicrotasks();

    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(2);
    expect(signalRTestState.builder.build).toHaveBeenCalledTimes(1);
  });

  it('does not let a late initial settings response overwrite authenticated settings', async () => {
    const initialSettings = createDeferredSettings();
    const authenticatedSettings = createDeferredSettings();
    signalRTestState.getSettings
      .mockReturnValueOnce(initialSettings.promise)
      .mockReturnValueOnce(authenticatedSettings.promise)
      .mockResolvedValue({ logLevel: 'Information', consoleLoggingEnabled: false });

    const { signalRService } = await import('../harvest-signalr');
    service = signalRService;

    window.dispatchEvent(new Event(AUTH_EVENT));
    await vi.waitFor(() => expect(signalRTestState.getSettings).toHaveBeenCalledTimes(2));

    authenticatedSettings.resolve({ logLevel: 'Information', consoleLoggingEnabled: false });
    await flushMicrotasks();
    initialSettings.resolve({ logLevel: 'Information', consoleLoggingEnabled: true });
    await flushMicrotasks();

    window.dispatchEvent(new Event(AUTH_EVENT));
    await flushMicrotasks();

    expect(signalRTestState.getSettings).toHaveBeenCalledTimes(3);
    expect(signalRTestState.builder.configureLogging).toHaveBeenCalledTimes(1);
    expect(signalRTestState.builder.configureLogging).toHaveBeenCalledWith(6);
  });

  it('queues another authenticated refresh when one is already active', async () => {
    const firstAuthenticatedSettings = createDeferredSettings();
    const secondAuthenticatedSettings = createDeferredSettings();
    signalRTestState.getSettings
      .mockResolvedValueOnce({ logLevel: 'Information', consoleLoggingEnabled: true })
      .mockReturnValueOnce(firstAuthenticatedSettings.promise)
      .mockReturnValueOnce(secondAuthenticatedSettings.promise);

    const { signalRService } = await import('../harvest-signalr');
    service = signalRService;
    await flushMicrotasks();

    window.dispatchEvent(new Event(AUTH_EVENT));
    await vi.waitFor(() => expect(signalRTestState.getSettings).toHaveBeenCalledTimes(2));
    window.dispatchEvent(new Event(AUTH_EVENT));

    firstAuthenticatedSettings.resolve({ logLevel: 'Warning', consoleLoggingEnabled: true });
    await vi.waitFor(() => expect(signalRTestState.getSettings).toHaveBeenCalledTimes(3));
    secondAuthenticatedSettings.resolve({ logLevel: 'Critical', consoleLoggingEnabled: true });
    await flushMicrotasks();

    expect(signalRTestState.builder.configureLogging).toHaveBeenNthCalledWith(1, 2);
    expect(signalRTestState.builder.configureLogging).toHaveBeenNthCalledWith(2, 3);
    expect(signalRTestState.builder.configureLogging).toHaveBeenNthCalledWith(3, 5);
  });
});
