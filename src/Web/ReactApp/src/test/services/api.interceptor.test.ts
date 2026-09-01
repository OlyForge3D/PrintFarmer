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
import { login } from '@/services/api/authApi';

const signalRSessionTestState = vi.hoisted(() => ({
  reset: vi.fn().mockResolvedValue(undefined),
}));
const authenticationExpirationTestState = vi.hoisted(() => ({
  notify: vi.fn(),
}));

vi.mock('@/common/auth/authenticatedSignalRSession', () => ({
  resetAuthenticatedSignalRSession: signalRSessionTestState.reset,
}));
vi.mock('@/common/auth/authenticationExpiration', () => ({
  notifyAuthenticationExpired: authenticationExpirationTestState.notify,
}));

// Do NOT mock axios — real interceptors must run.
// Mock only the URL helper so the constructor doesn't fail in jsdom.
vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: () => 'http://localhost:5245/api',
}));

/** Returns an adapter that always rejects with a 401 AxiosError. */
function make401Adapter(beforeReject?: () => void) {
  return (config: InternalAxiosRequestConfig) => {
    beforeReject?.();
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
    expect(authenticationExpirationTestState.notify).not.toHaveBeenCalled();
    expect(window.location.href).toBe('http://localhost/');
  });

  it('keeps the current identity when password login returns invalid credentials', async () => {
    localStorage.setItem('auth-token', 'current-identity-token');
    const removeItemSpy = vi.spyOn(localStorage, 'removeItem');

    await expect(login({
      username: 'different-user',
      password: 'invalid-password',
    })).rejects.toMatchObject({ statusCode: 401 });

    expect(localStorage.getItem('auth-token')).toBe('current-identity-token');
    expect(removeItemSpy).not.toHaveBeenCalledWith('auth-token');
    expect(signalRSessionTestState.reset).not.toHaveBeenCalled();
    expect(authenticationExpirationTestState.notify).not.toHaveBeenCalled();
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
    expect(removeItemSpy.mock.invocationCallOrder[0])
      .toBeLessThan(authenticationExpirationTestState.notify.mock.invocationCallOrder[0]);
    expect(window.location.href).toBe('/login');
  });

  it('invalidates the current identity when authenticated SignalR reset fails', async () => {
    const resetError = new AggregateError(
      [new Error('printer connection stop failed')],
      'Authenticated SignalR session reset failed.',
    );
    signalRSessionTestState.reset.mockRejectedValueOnce(resetError);
    localStorage.setItem('auth-token', 'test-token');
    const consoleErrorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined);

    const config: PfRequestConfig = { method: 'GET', url: '/test' };
    await expect(client.request(config)).rejects.toMatchObject({ statusCode: 401 });

    expect(consoleErrorSpy).toHaveBeenCalledWith(
      'Failed to reset authenticated SignalR session after a 401 response.',
      resetError,
    );
    expect(localStorage.getItem('auth-token')).toBeNull();
    expect(authenticationExpirationTestState.notify).toHaveBeenCalledOnce();
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
    expect(authenticationExpirationTestState.notify).toHaveBeenCalledOnce();
    expect(window.location.href).toBe('http://localhost/');
  });

  it('does not let a delayed 401 for token A invalidate newer token B', async () => {
    localStorage.setItem('auth-token', 'token-a');
    axiosInstance.defaults.adapter = make401Adapter(() => {
      localStorage.setItem('auth-token', 'token-b');
    });

    const config: PfRequestConfig = { method: 'GET', url: '/test' };
    await expect(client.request(config)).rejects.toMatchObject({ statusCode: 401 });

    expect(localStorage.getItem('auth-token')).toBe('token-b');
    expect(signalRSessionTestState.reset).not.toHaveBeenCalled();
    expect(authenticationExpirationTestState.notify).not.toHaveBeenCalled();
    expect(window.location.href).toBe('http://localhost/');
  });
});
