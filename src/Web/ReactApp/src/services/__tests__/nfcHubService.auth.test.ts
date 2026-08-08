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
    Connecting: 'Connecting',
    Disconnected: 'Disconnected',
  },
  LogLevel: {
    Warning: 3,
  },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: vi.fn((path: string) => path),
  getSignalRAccessToken: vi.fn(() => localStorage.getItem('auth-token') || ''),
}));

describe('nfcHubService authentication', () => {
  beforeEach(() => {
    // Re-evaluate the singleton after each mock setup; per-test isolation is intentional.
    vi.resetModules();
    vi.clearAllMocks();
    localStorage.clear();
  });

  it('uses the canonical auth token for the now-secured NFC hub', async () => {
    localStorage.setItem('auth-token', 'jwt-nfc');

    const { nfcHubService } = await import('../nfcHubService');
    await nfcHubService.ensureConnected();

    const options = signalRTestState.builder.withUrl.mock.calls[0][1] as {
      accessTokenFactory: () => string;
      withCredentials: boolean;
    };
    expect(options.accessTokenFactory()).toBe('jwt-nfc');
    expect(options.withCredentials).toBe(true);
  });
});
