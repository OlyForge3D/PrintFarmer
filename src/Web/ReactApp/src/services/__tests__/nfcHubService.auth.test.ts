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
  return { builder, connection };
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

const registeredTransports = vi.hoisted(() => new Map<string, () => Promise<void>>());

vi.mock('@/common/auth/authenticatedSignalRSession', () => ({
  registerAuthenticatedSignalRTransport: vi.fn(
    (name: string, reset: () => Promise<void>) => {
      registeredTransports.set(name, reset);
      return () => registeredTransports.delete(name);
    },
  ),
}));

describe('nfcHubService authentication', () => {
  beforeEach(() => {
    // Re-evaluate the singleton after each mock setup; per-test isolation is intentional.
    vi.resetModules();
    vi.clearAllMocks();
    localStorage.clear();
    registeredTransports.clear();
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

  it('registers itself as an authenticated transport so logout tears down an existing connection', async () => {
    await import('../nfcHubService');

    expect(registeredTransports.has('nfc-hub')).toBe(true);
  });

  it('stops the underlying connection when the authenticated session resets', async () => {
    const { nfcHubService } = await import('../nfcHubService');
    await nfcHubService.ensureConnected();

    const reset = registeredTransports.get('nfc-hub');
    expect(reset).toBeDefined();
    await reset!();

    expect(signalRTestState.connection.stop).toHaveBeenCalledOnce();
    expect(nfcHubService.isConnected()).toBe(false);
  });
});
