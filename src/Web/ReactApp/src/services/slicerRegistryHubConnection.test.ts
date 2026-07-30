import { beforeEach, describe, expect, it, vi } from 'vitest';

const testState = vi.hoisted(() => ({
  connection: {
    stop: vi.fn().mockResolvedValue(undefined),
  },
  withUrl: vi.fn(),
  register: vi.fn(),
  unregister: vi.fn(),
  reset: undefined as (() => Promise<void>) | undefined,
}));

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: class {
    withUrl(url: string, options: { accessTokenFactory: () => string }) {
      testState.withUrl(url, options);
      return this;
    }

    withAutomaticReconnect() {
      return this;
    }

    build() {
      return testState.connection;
    }
  },
}));

vi.mock('@/common/auth/authenticatedSignalRSession', () => ({
  registerAuthenticatedSignalRTransport: (
    name: string,
    reset: () => Promise<void>,
  ) => {
    testState.register(name);
    testState.reset = reset;
    return testState.unregister;
  },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: (path: string) => path,
  getSignalRAccessToken: () => localStorage.getItem('auth-token') || '',
}));

import { createSlicerRegistryConnection } from './slicerRegistryHubConnection';

describe('createSlicerRegistryConnection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    testState.reset = undefined;
    testState.connection.stop.mockResolvedValue(undefined);
  });

  it('uses the current JWT and participates in authenticated session resets', async () => {
    localStorage.setItem('auth-token', 'admin-token');
    const registered = createSlicerRegistryConnection('registry-page');

    expect(testState.withUrl).toHaveBeenCalledWith(
      '/hubs/slicer-registry',
      expect.objectContaining({ accessTokenFactory: expect.any(Function) }),
    );
    const options = testState.withUrl.mock.calls[0][1];
    expect(options.accessTokenFactory()).toBe('admin-token');
    expect(testState.register).toHaveBeenCalledWith('registry-page');

    await testState.reset?.();
    expect(testState.connection.stop).toHaveBeenCalledOnce();

    await registered.dispose();
    expect(testState.unregister).toHaveBeenCalledOnce();
    expect(testState.connection.stop).toHaveBeenCalledTimes(2);
  });
});
