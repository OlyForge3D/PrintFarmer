import { refresh } from '@/services/api/authApi';
import type { AuthenticationResult } from '@/types/api';

export const AUTH_REFRESH_TOKEN_KEY = 'auth-refresh-token';
export const AUTH_TOKEN_EXPIRY_KEY = 'auth-token-expires';
export const AUTH_USER_ID_KEY = 'auth-user-id';

let refreshInFlight: Promise<string | null> | null = null;
const refreshLockName = 'printfarmer-auth-refresh';
const fallbackRefreshLockKey = 'auth-refresh-lock';
const MAX_TIMEOUT_MS = 2_147_000_000;

export function storeAuthenticationResult(result: AuthenticationResult): void {
  if (result.token) localStorage.setItem('auth-token', result.token);
  if (result.refreshToken) localStorage.setItem(AUTH_REFRESH_TOKEN_KEY, result.refreshToken);
  else localStorage.removeItem(AUTH_REFRESH_TOKEN_KEY);
  const accessTokenExpiry = result.expiresAt ?? result.expires;
  const accessTokenExpiryMs = accessTokenExpiry ? new Date(accessTokenExpiry).getTime() : Number.NaN;
  if (Number.isFinite(accessTokenExpiryMs)) localStorage.setItem(AUTH_TOKEN_EXPIRY_KEY, String(accessTokenExpiryMs));
  else localStorage.removeItem(AUTH_TOKEN_EXPIRY_KEY);
  if (result.user?.id) localStorage.setItem(AUTH_USER_ID_KEY, result.user.id);
  if (typeof window !== 'undefined') window.dispatchEvent(new Event('printfarmer:auth-token-updated'));
}

export function clearStoredAuthentication(): void {
  localStorage.removeItem('auth-token');
  localStorage.removeItem(AUTH_REFRESH_TOKEN_KEY);
  localStorage.removeItem(AUTH_TOKEN_EXPIRY_KEY);
  localStorage.removeItem(AUTH_USER_ID_KEY);
}

async function refreshWithFallbackLock(observedRefreshToken: string): Promise<string | null> {
  const owner = `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const giveUpAt = Date.now() + 35_000;
  while (Date.now() < giveUpAt) {
    const currentToken = localStorage.getItem(AUTH_REFRESH_TOKEN_KEY);
    if (!currentToken) return null;
    if (currentToken !== observedRefreshToken) return localStorage.getItem('auth-token');

    const existing = localStorage.getItem(fallbackRefreshLockKey);
    let lock: { owner?: string; expiresAt?: number } | null = null;
    try { lock = existing ? JSON.parse(existing) as { owner?: string; expiresAt?: number } : null; } catch { /* Ignore malformed stale lock data. */ }
    if (!lock?.owner || !lock.expiresAt || lock.expiresAt <= Date.now()) {
      localStorage.setItem(fallbackRefreshLockKey, JSON.stringify({ owner, expiresAt: Date.now() + 40_000 }));
      const claimed = localStorage.getItem(fallbackRefreshLockKey);
      if (claimed && (JSON.parse(claimed) as { owner?: string }).owner === owner) {
        try {
          return await refreshUnderLock(observedRefreshToken);
        } finally {
          const latest = localStorage.getItem(fallbackRefreshLockKey);
          if (latest && (JSON.parse(latest) as { owner?: string }).owner === owner) {
            localStorage.removeItem(fallbackRefreshLockKey);
          }
        }
      }
    }
    await new Promise((resolve) => window.setTimeout(resolve, 50));
  }
  return null;
}

async function refreshUnderLock(observedRefreshToken: string): Promise<string | null> {
  const currentRefreshToken = localStorage.getItem(AUTH_REFRESH_TOKEN_KEY);
  if (!currentRefreshToken) return null;
  if (currentRefreshToken !== observedRefreshToken) return localStorage.getItem('auth-token');

  const result = await refresh(currentRefreshToken);
  if (!result.success || !result.token || !result.refreshToken) return null;
  storeAuthenticationResult(result);
  return result.token;
}

export function renewAccessToken(): Promise<string | null> {
  if (refreshInFlight) return refreshInFlight;
  const observedRefreshToken = localStorage.getItem(AUTH_REFRESH_TOKEN_KEY);
  if (!observedRefreshToken) return Promise.resolve(null);

  const renew = async () => {
    const lockManager = typeof navigator !== 'undefined' ? navigator.locks : undefined;
    if (lockManager?.request) {
      return lockManager.request(refreshLockName, () => refreshUnderLock(observedRefreshToken));
    }
    return refreshWithFallbackLock(observedRefreshToken);
  };

  refreshInFlight = renew().catch(() => null).finally(() => {
    refreshInFlight = null;
  });
  return refreshInFlight;
}

export function scheduleProactiveRenewal(onRenewalFailure: () => void): () => void {
  let timeoutId: number | undefined;
  let disposed = false;

  const schedule = () => {
    if (timeoutId !== undefined) window.clearTimeout(timeoutId);
    const expiresAt = Number(localStorage.getItem(AUTH_TOKEN_EXPIRY_KEY));
    if (!Number.isFinite(expiresAt) || expiresAt <= 0 || !localStorage.getItem(AUTH_REFRESH_TOKEN_KEY)) return;
    const remaining = expiresAt - Date.now();
    const delay = Math.max(0, Math.min(MAX_TIMEOUT_MS, Math.min(remaining - 5 * 60_000, remaining * 0.8)));
    timeoutId = window.setTimeout(async () => {
      if (disposed) return;
      if (!(await renewAccessToken())) onRenewalFailure();
      else schedule();
    }, delay);
  };

  const onVisibilityChange = () => {
    if (document.visibilityState === 'visible') {
      const expiresAt = Number(localStorage.getItem(AUTH_TOKEN_EXPIRY_KEY));
      if (Number.isFinite(expiresAt) && expiresAt - Date.now() <= 5 * 60_000) {
        void renewAccessToken().then((token) => { if (!token) onRenewalFailure(); else schedule(); });
      }
    }
  };
  schedule();
  document.addEventListener('visibilitychange', onVisibilityChange);
  window.addEventListener('storage', schedule);
  window.addEventListener('printfarmer:auth-token-updated', schedule);
  return () => {
    disposed = true;
    if (timeoutId !== undefined) window.clearTimeout(timeoutId);
    document.removeEventListener('visibilitychange', onVisibilityChange);
    window.removeEventListener('storage', schedule);
    window.removeEventListener('printfarmer:auth-token-updated', schedule);
  };
}
