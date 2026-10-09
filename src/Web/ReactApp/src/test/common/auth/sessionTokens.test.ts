import { AxiosError, AxiosHeaders, type AxiosResponse, type InternalAxiosRequestConfig } from 'axios';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { client } from '@/services/api/httpClient';
import { AUTH_REFRESH_TOKEN_KEY, AUTH_TOKEN_EXPIRY_KEY, clearStoredAuthentication, renewAccessToken, scheduleProactiveRenewal } from '@/common/auth/sessionTokens';

const originalAdapter = client.defaults.adapter;
const freshResult = {
  success: true,
  token: 'fresh-access-token',
  expiresAt: new Date(Date.now() + 60 * 60_000).toISOString(),
  refreshToken: 'rotated-refresh-token',
  refreshTokenExpires: new Date(Date.now() + 7 * 24 * 60 * 60_000).toISOString(),
  user: { id: 'user-1' },
};
let refreshAttempts = 0;
let refreshFails = false;

function unauthorized(config: InternalAxiosRequestConfig) {
  const response = {
    data: {}, status: 401, statusText: 'Unauthorized', headers: new AxiosHeaders(), config,
  } as AxiosResponse;
  return new AxiosError('Unauthorized', 'ERR_BAD_REQUEST', config, undefined, response);
}

describe('session token renewal', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('auth-token', 'expired-access-token');
    localStorage.setItem(AUTH_REFRESH_TOKEN_KEY, 'current-refresh-token');
    refreshAttempts = 0;
    refreshFails = false;
    client.defaults.adapter = async (config) => {
      if (config.url === '/auth/refresh') {
        refreshAttempts += 1;
        if (refreshFails) throw unauthorized(config);
        return { data: freshResult, status: 200, statusText: 'OK', headers: new AxiosHeaders(), config };
      }
      throw unauthorized(config);
    };
  });

  afterEach(() => {
    clearStoredAuthentication();
    client.defaults.adapter = originalAdapter;
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('shares one refresh request between concurrent 401 responses and retries each request once', async () => {
    let resourceAttempts = 0;
    client.defaults.adapter = async (config) => {
      if (config.url === '/auth/refresh') {
        refreshAttempts += 1;
        return { data: freshResult, status: 200, statusText: 'OK', headers: new AxiosHeaders(), config };
      }
      resourceAttempts += 1;
      if (config.headers.get('Authorization') === 'Bearer fresh-access-token') {
        return { data: { ok: true }, status: 200, statusText: 'OK', headers: new AxiosHeaders(), config };
      }
      throw unauthorized(config);
    };

    const [first, second, third] = await Promise.all([
      client.get('/resource-a'), client.get('/resource-b'), client.get('/resource-c'),
    ]);

    expect([first.status, second.status, third.status]).toEqual([200, 200, 200]);
    expect(resourceAttempts).toBe(6);
    expect(refreshAttempts).toBe(1);
    expect(localStorage.getItem('auth-token')).toBe('fresh-access-token');
    expect(localStorage.getItem(AUTH_REFRESH_TOKEN_KEY)).toBe('rotated-refresh-token');
  });

  it('stops after one retry and clears auth if the retried request still returns 401', async () => {
    let attempts = 0;
    client.defaults.adapter = async (config) => {
      if (config.url === '/auth/refresh') {
        refreshAttempts += 1;
        return { data: freshResult, status: 200, statusText: 'OK', headers: new AxiosHeaders(), config };
      }
      attempts += 1;
      throw unauthorized(config);
    };
    window.history.replaceState({}, '', '/login');

    await expect(client.get('/always-unauthorized')).rejects.toMatchObject({ statusCode: 401 });
    expect(attempts).toBe(2);
    expect(refreshAttempts).toBe(1);
    expect(localStorage.getItem('auth-token')).toBeNull();
    expect(localStorage.getItem(AUTH_REFRESH_TOKEN_KEY)).toBeNull();
  });

  it('renews proactively based on access-token expiry', async () => {
    vi.useFakeTimers();
    localStorage.setItem(AUTH_TOKEN_EXPIRY_KEY, String(Date.now() + 10 * 60_000));
    const stop = scheduleProactiveRenewal(vi.fn());

    await vi.advanceTimersByTimeAsync(5 * 60_000);

    expect(refreshAttempts).toBe(1);
    expect(localStorage.getItem(AUTH_REFRESH_TOKEN_KEY)).toBe('rotated-refresh-token');
    stop();
  });

  it('returns null when no refresh token exists', async () => {
    localStorage.removeItem(AUTH_REFRESH_TOKEN_KEY);
    expect(await renewAccessToken()).toBeNull();
    expect(refreshAttempts).toBe(0);
  });
});
