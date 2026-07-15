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
import { NotificationFrequency } from '@/types/api';
import type { AuthenticationResult, NotificationPreferencesDto, UserDto } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    getCurrentUser: vi.fn().mockRejectedValue(new Error('no session')),
    login: vi.fn(),
    logout: vi.fn().mockResolvedValue(undefined),
    register: vi.fn(),
    getNotificationPreferences: vi.fn(),
    updateNotificationPreferences: vi.fn(),
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

function Harness() {
  const { isAuthenticated, user, login, logout } = useAuth();
  return (
    <div>
      <button onClick={() => login({ username: 'alice', password: 'x' })}>login-a</button>
      <button onClick={() => login({ username: 'bob', password: 'x' })}>login-b</button>
      <button onClick={() => logout()}>logout</button>
      {isAuthenticated && <span data-testid="current-user">{user?.id}</span>}
      {isAuthenticated && <PreferencesConsumer />}
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
});
