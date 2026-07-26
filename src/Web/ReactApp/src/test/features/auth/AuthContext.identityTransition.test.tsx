/**
 * Regression coverage for #762: sensitive React Query cache leakage across a
 * soft SPA logout/login identity transition.
 *
 * These tests exercise the REAL AuthContext (login/logout) together with the
 * REAL shared QueryClient singleton and the REAL useNotificationPreferences
 * hook, proving that:
 *  - once identity B is authenticated, B can never read identity A's cached
 *    notification preferences (even momentarily, before B's own fetch
 *    resolves);
 *  - a stale in-flight fetch for A's data that resolves after logout cannot
 *    repopulate the cache for B to pick up;
 *  - unrelated/public query cache entries are left untouched by the
 *    logout/login transition.
 */
import React from 'react';
import { render, screen, act, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClientProvider } from '@tanstack/react-query';
import { queryClient } from '@/services/queryClient';
import { AuthProvider } from '@/common/contexts/AuthContext';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useNotificationPreferences } from '@/features/notifications/hooks/useNotificationPreferences';
import { useUserSettings, useUpdateUserSettings } from '@/features/settings/hooks/useUserSettings';
import { NotificationFrequency } from '@/types/api';
import type { AuthenticationResult, NotificationPreferencesDto, UserDto } from '@/types/api';
import type { UserSettingsResponse } from '@/features/settings/types';

const signalRSessionTestState = vi.hoisted(() => ({
  reset: vi.fn().mockResolvedValue(undefined),
}));

vi.mock('@/common/auth/authenticatedSignalRSession', () => ({
  resetAuthenticatedSignalRSession: signalRSessionTestState.reset,
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getCurrentUser: vi.fn().mockRejectedValue(new Error('no session')),
    login: vi.fn(),
    logout: vi.fn().mockResolvedValue(undefined),
    register: vi.fn(),
    getNotificationPreferences: vi.fn(),
    updateNotificationPreferences: vi.fn(),
    get: vi.fn(),
    put: vi.fn(),
  },
}));

import { apiClient } from '@/services/api';

const USER_A: UserDto = {
  id: 'user-a',
  username: 'alice',
  email: 'alice@example.com',
  isActive: true,
  emailConfirmed: true,
  createdAt: new Date('2024-01-01'),
  roles: [],
  permissions: [],
};

const USER_B: UserDto = {
  id: 'user-b',
  username: 'bob',
  email: 'bob@example.com',
  isActive: true,
  emailConfirmed: true,
  createdAt: new Date('2024-01-01'),
  roles: [],
  permissions: [],
};

function prefsFor(userId: string): NotificationPreferencesDto {
  return {
    userId,
    enableEmailNotifications: true,
    enablePushNotifications: false,
    enableInAppNotifications: true,
    enableTelegramNotifications: false,
    notifyOnCompletion: true,
    notifyOnFailure: true,
    notifyOnStart: false,
    notifyOnPause: false,
    eventChannelPreferences: [],
    frequency: NotificationFrequency.RealTime,
    retentionDays: 30,
  };
}

function PreferencesConsumer() {
  const { data, isLoading } = useNotificationPreferences();
  return (
    <div>
      {isLoading && <span data-testid="prefs-loading">loading</span>}
      {data && <span data-testid="prefs-owner">{data.userId}</span>}
    </div>
  );
}

function settingsFor(userId: string): UserSettingsResponse {
  return {
    userId,
    theme: 'dark',
    locale: 'en',
    itemsPerPage: 25,
    defaultSlicerPreset: null,
    printablesUsername: null,
    rowVersion: 'v1',
  };
}

function SettingsConsumer() {
  const { data } = useUserSettings();
  const mutation = useUpdateUserSettings();
  return (
    <div>
      {data && <span data-testid="settings-owner">{data.userId}</span>}
      <button onClick={() => mutation.mutate({ theme: 'light' })}>save-settings</button>
    </div>
  );
}

function Harness() {
  const { isAuthenticated, user, login, logout } = useAuth();
  return (
    <div>
      <button onClick={() => login({ username: 'alice', password: 'x' })}>login-a</button>
      <button onClick={() => login({ username: 'bob', password: 'x' })}>login-b</button>
      <button onClick={() => logout()}>logout</button>
      {isAuthenticated && <span data-testid="current-user">{user?.id}</span>}
      {isAuthenticated && <PreferencesConsumer />}
      {isAuthenticated && <SettingsConsumer />}
    </div>
  );
}

function renderHarness() {
  return render(
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <Harness />
      </AuthProvider>
    </QueryClientProvider>,
  );
}

describe('Identity transition cache isolation (#762)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    queryClient.clear();
    signalRSessionTestState.reset.mockResolvedValue(undefined);
    vi.mocked(apiClient.getCurrentUser).mockRejectedValue(new Error('no session'));
    vi.mocked(apiClient.logout).mockResolvedValue(undefined);
    vi.mocked(apiClient.login).mockImplementation(async (credentials): Promise<AuthenticationResult> => {
      if (credentials.username === 'alice') {
        return { success: true, token: 'token-a', user: USER_A };
      }
      if (credentials.username === 'bob') {
        return { success: true, token: 'token-b', user: USER_B };
      }
      return { success: false, error: 'invalid credentials' };
    });
    // Default settings mocks so tests that don't care about
    // useUserSettings/useUpdateUserSettings (mounted unconditionally by
    // <SettingsConsumer /> whenever authenticated) still resolve cleanly.
    vi.mocked(apiClient.get).mockResolvedValue({ data: settingsFor('unset') } as never);
    vi.mocked(apiClient.put).mockResolvedValue({ data: settingsFor('unset') } as never);
  });

  it('never exposes identity A cached preferences to identity B after logout/login', async () => {
    vi.mocked(apiClient.getNotificationPreferences).mockResolvedValue(prefsFor('user-a'));

    renderHarness();

    await act(async () => {
      screen.getByRole('button', { name: 'login-a' }).click();
    });

    await waitFor(() => expect(screen.getByTestId('current-user')).toHaveTextContent('user-a'));
    await waitFor(() => expect(screen.getByTestId('prefs-owner')).toHaveTextContent('user-a'));

    await act(async () => {
      screen.getByRole('button', { name: 'logout' }).click();
    });
    await waitFor(() => expect(screen.queryByTestId('current-user')).toBeNull());

    // Cache must be purged the moment logout completes — before any next
    // login can render authenticated UI against it.
    expect(queryClient.getQueryData(['notifications', 'preferences'])).toBeUndefined();

    // Hold B's fetch open so we can inspect the DOM/cache in the window
    // between "B is authenticated" and "B's own fetch resolved".
    let resolveBPrefs: (value: NotificationPreferencesDto) => void = () => {};
    vi.mocked(apiClient.getNotificationPreferences).mockImplementation(
      () => new Promise((resolve) => { resolveBPrefs = resolve; }),
    );

    await act(async () => {
      screen.getByRole('button', { name: 'login-b' }).click();
    });
    await waitFor(() => expect(screen.getByTestId('current-user')).toHaveTextContent('user-b'));

    // B is authenticated but its own fetch hasn't resolved yet: there must be
    // no stale A data visible, in the DOM or in the cache.
    expect(screen.queryByTestId('prefs-owner')).toBeNull();
    expect(screen.getByTestId('prefs-loading')).toBeInTheDocument();
    expect(queryClient.getQueryData(['notifications', 'preferences'])).toBeUndefined();

    await act(async () => {
      resolveBPrefs(prefsFor('user-b'));
    });
    await waitFor(() => expect(screen.getByTestId('prefs-owner')).toHaveTextContent('user-b'));
  });

  it('resets authenticated hubs before replacing or removing an identity token', async () => {
    vi.mocked(apiClient.getNotificationPreferences).mockResolvedValue(prefsFor('user-a'));
    const setItem = vi.spyOn(localStorage, 'setItem');
    const removeItem = vi.spyOn(localStorage, 'removeItem');

    renderHarness();

    await act(async () => {
      screen.getByRole('button', { name: 'login-a' }).click();
    });
    await waitFor(() => expect(screen.getByTestId('current-user')).toHaveTextContent('user-a'));

    await act(async () => {
      screen.getByRole('button', { name: 'logout' }).click();
    });
    await waitFor(() => expect(screen.queryByTestId('current-user')).toBeNull());

    vi.mocked(apiClient.getNotificationPreferences).mockResolvedValue(prefsFor('user-b'));
    await act(async () => {
      screen.getByRole('button', { name: 'login-b' }).click();
    });
    await waitFor(() => expect(screen.getByTestId('current-user')).toHaveTextContent('user-b'));

    expect(signalRSessionTestState.reset).toHaveBeenCalledTimes(3);
    const resetOrders = signalRSessionTestState.reset.mock.invocationCallOrder;
    const tokenWrites = setItem.mock.calls
      .map((call, index) => ({ call, order: setItem.mock.invocationCallOrder[index] }))
      .filter(({ call }) => call[0] === 'auth-token');
    const tokenRemovalIndex = removeItem.mock.calls.findIndex(call => call[0] === 'auth-token');

    expect(resetOrders[0]).toBeLessThan(tokenWrites[0].order);
    expect(resetOrders[1]).toBeLessThan(removeItem.mock.invocationCallOrder[tokenRemovalIndex]);
    expect(resetOrders[2]).toBeLessThan(tokenWrites[1].order);
    expect(tokenWrites.map(({ call }) => call[1])).toEqual(['token-a', 'token-b']);
  });

  it('resets hubs and sensitive state when another tab replaces or removes the token', async () => {
    vi.mocked(apiClient.getNotificationPreferences).mockResolvedValue(prefsFor('user-a'));
    renderHarness();

    await act(async () => {
      screen.getByRole('button', { name: 'login-a' }).click();
    });
    await waitFor(() => expect(screen.getByTestId('current-user')).toHaveTextContent('user-a'));

    signalRSessionTestState.reset.mockClear();
    vi.mocked(apiClient.getCurrentUser).mockClear();
    let resolveUserB: (user: UserDto) => void = () => {};
    vi.mocked(apiClient.getCurrentUser).mockImplementation(
      () => new Promise(resolve => { resolveUserB = resolve; }),
    );
    queryClient.setQueryData(['notifications', 'preferences'], prefsFor('user-a'));

    await act(async () => {
      localStorage.setItem('auth-token', 'token-b');
      window.dispatchEvent(new StorageEvent('storage', {
        key: 'auth-token',
        oldValue: 'token-a',
        newValue: 'token-b',
      }));
    });

    await waitFor(() => expect(signalRSessionTestState.reset).toHaveBeenCalledTimes(1));
    await waitFor(() =>
      expect(queryClient.getQueryData(['notifications', 'preferences'])).toBeUndefined());
    vi.mocked(apiClient.getNotificationPreferences).mockResolvedValue(prefsFor('user-b'));
    await act(async () => {
      resolveUserB(USER_B);
    });
    await waitFor(() => expect(screen.getByTestId('current-user')).toHaveTextContent('user-b'));
    expect(signalRSessionTestState.reset.mock.invocationCallOrder[0])
      .toBeLessThan(vi.mocked(apiClient.getCurrentUser).mock.invocationCallOrder[0]);

    await act(async () => {
      localStorage.removeItem('auth-token');
      window.dispatchEvent(new StorageEvent('storage', {
        key: 'auth-token',
        oldValue: 'token-b',
        newValue: null,
      }));
    });

    await waitFor(() => expect(screen.queryByTestId('current-user')).toBeNull());
    await waitFor(() => expect(signalRSessionTestState.reset).toHaveBeenCalledTimes(2));
    expect(queryClient.getQueryData(['notifications', 'preferences'])).toBeUndefined();
  });

  it('discards a stale in-flight fetch for the previous identity instead of letting it repopulate the cache', async () => {
    vi.mocked(apiClient.getNotificationPreferences).mockResolvedValue(prefsFor('user-a'));

    renderHarness();

    await act(async () => {
      screen.getByRole('button', { name: 'login-a' }).click();
    });
    await waitFor(() => expect(screen.getByTestId('prefs-owner')).toHaveTextContent('user-a'));

    // Trigger a background refetch for A's preferences that we control the
    // timing of, then log out while it is still in flight.
    let resolveStaleFetch: (value: NotificationPreferencesDto) => void = () => {};
    vi.mocked(apiClient.getNotificationPreferences).mockImplementation(
      () => new Promise((resolve) => { resolveStaleFetch = resolve; }),
    );
    await act(async () => {
      queryClient.invalidateQueries({ queryKey: ['notifications', 'preferences'] });
    });

    await act(async () => {
      screen.getByRole('button', { name: 'logout' }).click();
    });
    await waitFor(() => expect(screen.queryByTestId('current-user')).toBeNull());

    // Now let the stale in-flight response for A resolve, after logout has
    // already cancelled/removed the cache entry.
    await act(async () => {
      resolveStaleFetch(prefsFor('user-a'));
    });

    expect(queryClient.getQueryData(['notifications', 'preferences'])).toBeUndefined();
  });

  it('leaves unrelated public/shared query cache entries untouched by logout', async () => {
    vi.mocked(apiClient.getNotificationPreferences).mockResolvedValue(prefsFor('user-a'));
    queryClient.setQueryData(['printers'], [{ id: 'printer-1' }]);
    queryClient.setQueryData(['settings', 'farm'], { name: 'My Farm' });

    renderHarness();

    await act(async () => {
      screen.getByRole('button', { name: 'login-a' }).click();
    });
    await waitFor(() => expect(screen.getByTestId('current-user')).toHaveTextContent('user-a'));

    await act(async () => {
      screen.getByRole('button', { name: 'logout' }).click();
    });
    await waitFor(() => expect(screen.queryByTestId('current-user')).toBeNull());

    expect(queryClient.getQueryData(['printers'])).toEqual([{ id: 'printer-1' }]);
    expect(queryClient.getQueryData(['settings', 'farm'])).toEqual({ name: 'My Farm' });
  });

  it('discards a dirty-form settings save started by A if it resolves after A logs out, instead of repopulating the shared cache', async () => {
    vi.mocked(apiClient.getNotificationPreferences).mockResolvedValue(prefsFor('user-a'));
    vi.mocked(apiClient.get).mockResolvedValue({ data: settingsFor('user-a') } as never);

    renderHarness();

    await act(async () => {
      screen.getByRole('button', { name: 'login-a' }).click();
    });
    await waitFor(() => expect(screen.getByTestId('settings-owner')).toHaveTextContent('user-a'));

    // A edits the form and clicks save, but the PUT response is slow — hold
    // it open so we can log out before it resolves ("dirty-form timing").
    let resolveSave: (value: { data: UserSettingsResponse }) => void = () => {};
    vi.mocked(apiClient.put).mockImplementation(
      () => new Promise((resolve) => { resolveSave = resolve; }),
    );
    await act(async () => {
      screen.getByRole('button', { name: 'save-settings' }).click();
    });

    await act(async () => {
      screen.getByRole('button', { name: 'logout' }).click();
    });
    await waitFor(() => expect(screen.queryByTestId('current-user')).toBeNull());
    expect(queryClient.getQueryData(['settings', 'user'])).toBeUndefined();

    // A's save now resolves, after logout has already purged the cache and
    // bumped the auth epoch. The mutation's onSuccess must detect the
    // identity transition and discard the response rather than writing A's
    // data back into the shared ['settings', 'user'] cache key.
    await act(async () => {
      resolveSave({ data: settingsFor('user-a') });
    });

    expect(queryClient.getQueryData(['settings', 'user'])).toBeUndefined();
  });
});
