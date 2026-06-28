import { beforeEach, describe, expect, it, vi } from 'vitest';

const hubTestState = vi.hoisted(() => {
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
  return { builder, connection };
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: function MockHubConnectionBuilder() {
    return hubTestState.builder;
  },
  HttpTransportType: { WebSockets: 1, ServerSentEvents: 2, LongPolling: 4 },
  LogLevel: { Information: 2 },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: vi.fn(() => 'http://localhost:5245/hubs/slicers'),
}));

describe('slicerHubService auth token', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it('uses the canonical "auth-token" localStorage key for the access token', async () => {
    // Regression: the factory previously read "authToken" (camelCase), a key that is
    // never written, so the slicer hub negotiate always sent an empty bearer -> 401.
    localStorage.setItem('auth-token', 'jwt-abc');

    const { slicerHubService } = await import('@/services/slicerHubService');
    await slicerHubService.start();

    const options = hubTestState.builder.withUrl.mock.calls[0][1] as {
      accessTokenFactory: () => string;
    };
    expect(options.accessTokenFactory()).toBe('jwt-abc');

    await slicerHubService.stop();
  });

  it('returns an empty string (not the wrong key) when no token is stored', async () => {
    localStorage.setItem('authToken', 'wrong-key-should-be-ignored');

    const { slicerHubService } = await import('@/services/slicerHubService');
    await slicerHubService.start();

    const options = hubTestState.builder.withUrl.mock.calls[0][1] as {
      accessTokenFactory: () => string;
    };
    expect(options.accessTokenFactory()).toBe('');

    await slicerHubService.stop();
  });
});
