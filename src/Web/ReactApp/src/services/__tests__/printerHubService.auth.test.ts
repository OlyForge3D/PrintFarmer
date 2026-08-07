import { beforeEach, describe, expect, it, vi } from 'vitest';

const signalRTestState = vi.hoisted(() => {
  const connection = {
    on: vi.fn(),
    onclose: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    state: 'Disconnected',
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
  return { builder };
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: function MockHubConnectionBuilder() {
    return signalRTestState.builder;
  },
  HubConnectionState: {
    Connected: 'Connected',
  },
  HttpTransportType: {
    WebSockets: 1,
    ServerSentEvents: 2,
    LongPolling: 4,
  },
  LogLevel: {
    Information: 2,
  },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getSignalRAccessToken: vi.fn(() => localStorage.getItem('auth-token') || ''),
}));

describe('printerHubService authentication', () => {
  beforeEach(() => {
    // Re-evaluate the singleton after each mock setup; per-test isolation is intentional.
    vi.resetModules();
    vi.clearAllMocks();
    localStorage.clear();
  });

  it('uses the canonical auth token for the secured printer hub', async () => {
    localStorage.setItem('auth-token', 'jwt-import');

    const { printerHubService } = await import('../printerHubService');
    await printerHubService.start();

    const options = signalRTestState.builder.withUrl.mock.calls[0][1] as {
      accessTokenFactory: () => string;
    };
    expect(options.accessTokenFactory()).toBe('jwt-import');

    await printerHubService.stop();
  });
});
