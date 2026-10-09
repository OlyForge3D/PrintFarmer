import { useContext } from 'react';
import { act, renderHook, waitFor } from '@testing-library/react';
import { AxiosError, AxiosHeaders, type InternalAxiosRequestConfig } from 'axios';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthContext, AuthProvider } from '@/common/contexts/AuthContext';
import { AUTH_REFRESH_TOKEN_KEY, AUTH_TOKEN_EXPIRY_KEY, renewAccessToken } from '@/common/auth/sessionTokens';
import { client } from '@/services/api/httpClient';

vi.mock('@/common/auth/authenticatedSignalRSession', () => ({
  resetAuthenticatedSignalRSession: vi.fn().mockResolvedValue(undefined),
}));
vi.mock('@/common/auth/sensitiveQueryCache', () => ({
  clearSensitiveUserQueries: vi.fn().mockResolvedValue(undefined),
}));
vi.mock('@/services/passkeyService', () => ({ loginWithPasskey: vi.fn() }));

const originalAdapter = client.defaults.adapter;
const user = {
  id: 'user-1', username: 'alice', email: 'alice@example.com', isActive: true,
  emailConfirmed: true, createdAt: new Date(), roles: [], permissions: [],
};
const freshResult = {
  success: true, token: 'renewed-access', refreshToken: 'renewed-refresh', user,
  expiresAt: new Date(Date.now() + 60 * 60_000).toISOString(),
};

function response(config: InternalAxiosRequestConfig, data: unknown) {
  return { config, data, status: 200, statusText: 'OK', headers: new AxiosHeaders() };
}

async function authenticatedContext() {
  const hook = renderHook(() => useContext(AuthContext), { wrapper: AuthProvider });
  await waitFor(() => expect(hook.result.current?.isAuthenticated).toBe(true));
  return hook;
}

beforeEach(() => {
  localStorage.clear();
  localStorage.setItem('auth-token', 'current-access');
  localStorage.setItem(AUTH_TOKEN_EXPIRY_KEY, String(Date.now() + 60 * 60_000));
  localStorage.setItem(AUTH_REFRESH_TOKEN_KEY, 'current-refresh');
});

afterEach(() => {
  client.defaults.adapter = originalAdapter;
  localStorage.clear();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

describe('coordinated logout', () => {
  it.each(['expired', 'near-expiry', 'missing'] as const)(
    'renews a %s access token once before logout with the rotated tokens', async (scenario) => {
      const requests: string[] = [];
      client.defaults.adapter = async (config) => {
        requests.push(config.url ?? '');
        if (config.url === '/auth/me') return response(config, user);
        if (config.url === '/auth/refresh') {
          expect(JSON.parse(config.data)).toEqual({ refreshToken: 'latest-refresh' });
          return response(config, freshResult);
        }
        expect(config.url).toBe('/auth/logout');
        expect(config.headers.get('Authorization')).toBe(['Bearer', freshResult.token].join(' '));
        expect(JSON.parse(config.data)).toEqual({ refreshToken: freshResult.refreshToken });
        expect(config.skipAuthRedirect).toBe(true);
        return response(config, {});
      };
      const { result } = await authenticatedContext();
      localStorage.setItem(AUTH_REFRESH_TOKEN_KEY, 'latest-refresh');
      localStorage.setItem(AUTH_TOKEN_EXPIRY_KEY, String(Date.now() + (scenario === 'expired' ? -1 : 60_000)));
      if (scenario === 'missing') localStorage.removeItem('auth-token');

      await act(async () => { await result.current?.logout(); });

      expect(requests).toEqual(['/auth/me', '/auth/refresh', '/auth/logout']);
      expect(localStorage.getItem('auth-token')).toBeNull();
      expect(localStorage.getItem(AUTH_REFRESH_TOKEN_KEY)).toBeNull();
      expect(result.current?.isAuthenticated).toBe(false);
      expect(result.current?.isLoading).toBe(false);
    },
  );

  it.each([true, false])('renews and retries logout only once after an unexpected 401 (retry succeeds: %s)', async (retrySucceeds) => {
    const requests: string[] = [];
    let logoutAttempts = 0;
    client.defaults.adapter = async (config) => {
      requests.push(config.url ?? '');
      if (config.url === '/auth/me') return response(config, user);
      if (config.url === '/auth/refresh') return response(config, freshResult);
      logoutAttempts += 1;
      if (logoutAttempts === 2) {
        expect(config.headers.get('Authorization')).toBe(['Bearer', freshResult.token].join(' '));
        expect(JSON.parse(config.data)).toEqual({ refreshToken: freshResult.refreshToken });
        if (retrySucceeds) return response(config, {});
      }
      throw new AxiosError('Unauthorized', 'ERR_BAD_REQUEST', config, undefined, {
        ...response(config, {}), status: 401,
      });
    };
    const { result } = await authenticatedContext();

    await act(async () => { await result.current?.logout(); });

    expect(requests).toEqual(['/auth/me', '/auth/logout', '/auth/refresh', '/auth/logout']);
    expect(localStorage.getItem('auth-token')).toBeNull();
    expect(localStorage.getItem(AUTH_REFRESH_TOKEN_KEY)).toBeNull();
    expect(result.current?.isAuthenticated).toBe(false);
    expect(result.current?.isLoading).toBe(false);
  });

  it('does not renew again when logout returns 401 after preflight renewal', async () => {
    const requests: string[] = [];
    client.defaults.adapter = async (config) => {
      requests.push(config.url ?? '');
      if (config.url === '/auth/me') return response(config, user);
      if (config.url === '/auth/refresh') return response(config, freshResult);
      throw new AxiosError('Unauthorized', 'ERR_BAD_REQUEST', config, undefined, {
        ...response(config, {}), status: 401,
      });
    };
    const { result } = await authenticatedContext();
    localStorage.setItem(AUTH_TOKEN_EXPIRY_KEY, String(Date.now() - 1));

    await act(async () => { await result.current?.logout(); });

    expect(requests).toEqual(['/auth/me', '/auth/refresh', '/auth/logout']);
    expect(localStorage.getItem('auth-token')).toBeNull();
    expect(localStorage.getItem(AUTH_REFRESH_TOKEN_KEY)).toBeNull();
    expect(result.current?.isAuthenticated).toBe(false);
  });

  it.each(['before-logout', 'after-401'] as const)(
    'clears local state without a logout loop if renewal fails %s', async (scenario) => {
      const requests: string[] = [];
      client.defaults.adapter = async (config) => {
        requests.push(config.url ?? '');
        if (config.url === '/auth/me') return response(config, user);
        throw new AxiosError('Unauthorized', 'ERR_BAD_REQUEST', config, undefined, {
          ...response(config, {}), status: 401,
        });
      };
      const { result } = await authenticatedContext();
      if (scenario === 'before-logout') localStorage.setItem(AUTH_TOKEN_EXPIRY_KEY, String(Date.now() - 1));

      await act(async () => { await result.current?.logout(); });

      expect(requests).toEqual(scenario === 'before-logout'
        ? ['/auth/me', '/auth/refresh']
        : ['/auth/me', '/auth/logout', '/auth/refresh']);
      expect(localStorage.getItem('auth-token')).toBeNull();
      expect(localStorage.getItem(AUTH_REFRESH_TOKEN_KEY)).toBeNull();
      expect(result.current?.isAuthenticated).toBe(false);
      expect(result.current?.isLoading).toBe(false);
    },
  );

  it('waits for in-flight renewal and sends its rotated token, then clears the renewed local session', async () => {
    let completeRenewal!: () => void;
    const renewalResponse = new Promise<void>(resolve => { completeRenewal = resolve; });
    const sentLogout = vi.fn();
    const refreshing = vi.fn();
    client.defaults.adapter = async (config) => {
      if (config.url === '/auth/me') return response(config, user);
      if (config.url === '/auth/refresh') {
        refreshing();
        await renewalResponse;
        return response(config, freshResult);
      }
      sentLogout(JSON.parse(config.data));
      return response(config, {});
    };
    const { result } = await authenticatedContext();
    const renewal = renewAccessToken();
    await waitFor(() => expect(refreshing).toHaveBeenCalledOnce());
    let loggingOut!: Promise<void>;
    act(() => { loggingOut = result.current!.logout(); });
    expect(sentLogout).not.toHaveBeenCalled();

    await act(async () => {
      completeRenewal();
      await renewal;
      await loggingOut;
    });

    expect(sentLogout).toHaveBeenCalledExactlyOnceWith({ refreshToken: 'renewed-refresh' });
    expect(localStorage.getItem('auth-token')).toBeNull();
    expect(localStorage.getItem(AUTH_REFRESH_TOKEN_KEY)).toBeNull();
    expect(result.current?.isAuthenticated).toBe(false);
  });

  it('uses the native cross-tab renewal lock and reads the token only after acquisition', async () => {
    const sentLogout = vi.fn();
    client.defaults.adapter = async (config) => {
      if (config.url === '/auth/me') return response(config, user);
      sentLogout(JSON.parse(config.data));
      return response(config, {});
    };
    const { result } = await authenticatedContext();
    let releaseLock!: () => void;
    const lockReleased = new Promise<void>(resolve => { releaseLock = resolve; });
    const requestLock = vi.fn(async (_name: string, action: () => Promise<void>) => {
      await lockReleased;
      return action();
    });
    vi.stubGlobal('navigator', { locks: { request: requestLock } });
    let loggingOut!: Promise<void>;
    act(() => { loggingOut = result.current!.logout(); });
    expect(requestLock).toHaveBeenCalledWith('printfarmer-auth-refresh', expect.any(Function));
    expect(sentLogout).not.toHaveBeenCalled();
    localStorage.setItem('auth-token', 'other-tab-access');
    localStorage.setItem(AUTH_REFRESH_TOKEN_KEY, 'other-tab-refresh');

    await act(async () => {
      releaseLock();
      await loggingOut;
    });

    expect(sentLogout).toHaveBeenCalledExactlyOnceWith({ refreshToken: 'other-tab-refresh' });
    expect(localStorage.getItem('auth-token')).toBeNull();
    expect(result.current?.isAuthenticated).toBe(false);
  });

  it('waits for another tab holding the fallback renewal lock before reading the refresh token', async () => {
    const sentLogout = vi.fn();
    client.defaults.adapter = async (config) => {
      if (config.url === '/auth/me') return response(config, user);
      sentLogout(JSON.parse(config.data));
      return response(config, {});
    };
    const { result } = await authenticatedContext();
    vi.useFakeTimers();
    localStorage.setItem('auth-refresh-lock', JSON.stringify({ owner: 'other-tab', expiresAt: Date.now() + 40_000 }));
    let loggingOut!: Promise<void>;
    act(() => { loggingOut = result.current!.logout(); });
    expect(sentLogout).not.toHaveBeenCalled();
    localStorage.setItem('auth-token', 'other-tab-access');
    localStorage.setItem(AUTH_REFRESH_TOKEN_KEY, 'other-tab-refresh');
    localStorage.removeItem('auth-refresh-lock');

    await act(async () => {
      await vi.advanceTimersByTimeAsync(50);
      await loggingOut;
    });

    expect(sentLogout).toHaveBeenCalledExactlyOnceWith({ refreshToken: 'other-tab-refresh' });
    expect(localStorage.getItem('auth-token')).toBeNull();
    expect(result.current?.isAuthenticated).toBe(false);
  });
});
