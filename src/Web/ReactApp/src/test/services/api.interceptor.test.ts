/**
 * Tests the ApiClient 401 response interceptor behaviour with the
 * `skipAuthRedirect` flag introduced on the passkey request config.
 *
 * Real axios is used (not mocked) so the interceptors actually run.
 * A custom adapter injects a controlled 401 AxiosError without a network call.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AxiosError } from 'axios';
import type { AxiosInstance, InternalAxiosRequestConfig } from 'axios';
import { ApiClient } from '@/services/api';
import type { PfRequestConfig } from '@/services/api';

const signalRSessionTestState = vi.hoisted(() => ({
  reset: vi.fn().mockResolvedValue(undefined),
}));

vi.mock('@/common/auth/authenticatedSignalRSession', () => ({
  resetAuthenticatedSignalRSession: signalRSessionTestState.reset,
}));

// Do NOT mock axios — real interceptors must run.
// Mock only the URL helper so the constructor doesn't fail in jsdom.
vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: () => 'http://localhost:5245/api',
}));

/** Returns an adapter that always rejects with a 401 AxiosError. */
function make401Adapter() {
  return (config: InternalAxiosRequestConfig) => {
    const err = new AxiosError(
      'Request failed with status code 401',
      'ERR_BAD_REQUEST',
      config,
      undefined,
      {
        status: 401,
        data: { error: 'Unauthorized' },
        headers: {},
        config,
        statusText: 'Unauthorized',
      },
    );
    return Promise.reject(err);
  };
}

describe('ApiClient — 401 response interceptor', () => {
  let client: ApiClient;
  let axiosInstance: AxiosInstance;

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    signalRSessionTestState.reset.mockResolvedValue(undefined);

    // Replace window.location with a plain writable object so we can observe
    // href assignments without jsdom attempting an actual navigation.
    Object.defineProperty(window, 'location', {
      value: {
        pathname: '/',
        href: 'http://localhost/',
        assign: vi.fn(),
      },
      writable: true,
      configurable: true,
    });

    client = new ApiClient();
    axiosInstance = (client as unknown as { client: AxiosInstance }).client;
    axiosInstance.defaults.adapter = make401Adapter();
  });

  it('does not clear the token or redirect when skipAuthRedirect is true', async () => {
    localStorage.setItem('auth-token', 'test-token');
    const removeItemSpy = vi.spyOn(localStorage, 'removeItem');

    const config: PfRequestConfig = { method: 'GET', url: '/test', skipAuthRedirect: true };
    await expect(client.request(config)).rejects.toMatchObject({ statusCode: 401 });

    expect(removeItemSpy).not.toHaveBeenCalledWith('auth-token');
    expect(signalRSessionTestState.reset).not.toHaveBeenCalled();
    expect(window.location.href).toBe('http://localhost/');
  });

  it('clears the token and redirects to /login on 401 without skipAuthRedirect', async () => {
    localStorage.setItem('auth-token', 'test-token');
    const removeItemSpy = vi.spyOn(localStorage, 'removeItem');

    const config: PfRequestConfig = { method: 'GET', url: '/test' };
    await expect(client.request(config)).rejects.toMatchObject({ statusCode: 401 });

    expect(removeItemSpy).toHaveBeenCalledWith('auth-token');
    expect(signalRSessionTestState.reset).toHaveBeenCalledOnce();
    expect(signalRSessionTestState.reset.mock.invocationCallOrder[0])
      .toBeLessThan(removeItemSpy.mock.invocationCallOrder[0]);
    expect(window.location.href).toBe('/login');
  });

  it('resets authenticated hubs before clearing a token on an auth page without navigation', async () => {
    localStorage.setItem('auth-token', 'test-token');
    window.location.pathname = '/login';
    const removeItemSpy = vi.spyOn(localStorage, 'removeItem');

    const config: PfRequestConfig = { method: 'GET', url: '/test' };
    await expect(client.request(config)).rejects.toMatchObject({ statusCode: 401 });

    expect(signalRSessionTestState.reset).toHaveBeenCalledOnce();
    expect(signalRSessionTestState.reset.mock.invocationCallOrder[0])
      .toBeLessThan(removeItemSpy.mock.invocationCallOrder[0]);
    expect(window.location.href).toBe('http://localhost/');
  });
});
