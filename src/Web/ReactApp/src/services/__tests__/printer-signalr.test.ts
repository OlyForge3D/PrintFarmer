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
