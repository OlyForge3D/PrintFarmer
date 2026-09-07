/**
 * Permission-filtering behaviour tests for the shared workspace search index
 * (#2505). Both the modal command palette and the persistent workspace
 * search consume this hook, so its permission handling is tested directly
 * against real permissioned fixtures rather than only through the palette's
 * mocked output.
 */
import { describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useSettingsSearchIndex } from '@/features/settings/hooks/useSettingsSearchIndex';

const authState: {
  user: { id: string; email: string; isActive: boolean; roles: string[] } | null;
  roles: string[];
  grant?: string;
} = {
  user: { id: '1', email: 'admin@test.com', isActive: true, roles: ['farm_admin'] },
  roles: ['farm_admin'],
};

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    user: authState.user,
    isAuthenticated: authState.user !== null,
    isLoading: false,
    hasRole: (role: string) => authState.roles.includes(role),
    hasPermission: (resource: string, action: string) => authState.roles.includes('farm_admin')
      || authState.grant === `${resource}:${action}`,
  }),
}));

vi.mock('@/features/settings/queries/useSettingsMetadata', () => ({
  useSettingsMetadata: () => ({
    data: [{
      key: 'SystemLog',
      className: 'SystemLogSettings',
      group: 'System',
      properties: [{ name: 'Enabled', type: 'boolean', display: { name: 'Enable System Logging' } }],
    }],
    isLoading: false,
    isError: false,
    refetch: vi.fn(),
  }),
  useSettingsGroups: () => ({
    data: [{ key: 'System', displayName: 'System', order: 0 }],
    isLoading: false,
    isError: false,
    refetch: vi.fn(),
  }),
}));

function wrapper({ children }: { children: ReactNode }) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

describe('useSettingsSearchIndex', () => {
  it('gives a farm_admin the full index: destinations, user settings-nav, and field results', async () => {
    authState.user = { id: '1', email: 'admin@test.com', isActive: true, roles: ['farm_admin'] };
    authState.roles = ['farm_admin'];
    authState.grant = undefined;

    const { result } = renderHook(() => useSettingsSearchIndex({ enabled: true }), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    expect(result.current.destinationItems.some((item) => item.label === 'Login Audit')).toBe(true);
    expect(result.current.settingsNavItems.every((item) => item.scopeId === 'user')).toBe(true);
    expect(result.current.settingFieldItems.some((item) => item.id === 'setting.SystemLog.Enabled')).toBe(true);
    expect(result.current.items.length).toBe(
      result.current.destinationItems.length + result.current.settingsNavItems.length + result.current.settingFieldItems.length,
    );
  });

  it('hides field results from a delegate without system_settings:admin', async () => {
    authState.user = { id: '2', email: 'delegate@test.com', isActive: true, roles: ['farm_user'] };
    authState.roles = ['farm_user'];
    authState.grant = 'printers:admin';

    const { result } = renderHook(() => useSettingsSearchIndex({ enabled: true }), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    expect(result.current.settingFieldItems).toEqual([]);
    // The delegate's single-resource grant still surfaces the matching
    // destination (Printer Groups requires `printers:admin`) without
    // surfacing destinations that need a different permission.
    expect(result.current.destinationItems.some((item) => item.label === 'Printer Groups')).toBe(true);
    expect(result.current.destinationItems.some((item) => item.label === 'Login Audit')).toBe(false);
  });

  it('surfaces field results for a delegate granted only system_settings:admin', async () => {
    authState.user = { id: '3', email: 'settings-delegate@test.com', isActive: true, roles: ['farm_user'] };
    authState.roles = ['farm_user'];
    authState.grant = 'system_settings:admin';

    const { result } = renderHook(() => useSettingsSearchIndex({ enabled: true }), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    expect(result.current.settingFieldItems.some((item) => item.id === 'setting.SystemLog.Enabled')).toBe(true);
    expect(result.current.destinationItems.some((item) => item.label === 'Login Audit')).toBe(true);
  });

  it('collapses permission-gated lists to empty immediately on logout — no stale unauthorized names flash', async () => {
    authState.user = null;
    authState.roles = [];
    authState.grant = undefined;

    const { result } = renderHook(() => useSettingsSearchIndex({ enabled: true }), { wrapper });

    // Admin destinations and metadata-driven field results require a signed-in
    // user, so both collapse to [] the instant `user` goes null.
    expect(result.current.destinationItems).toEqual([]);
    expect(result.current.settingFieldItems).toEqual([]);
    // The static user-scope settings-nav list (e.g. "Profile", "API Keys") is
    // not permission-gated by itself — it stays populated, matching the
    // pre-#2505 palette behavior; only the two lists above depend on auth.
    expect(result.current.settingsNavItems.length).toBeGreaterThan(0);
    expect(result.current.isLoading).toBe(false);
    expect(result.current.isError).toBe(false);
  });

  it('does not report loading when enabled is false, even for an authenticated farm_admin', async () => {
    authState.user = { id: '1', email: 'admin@test.com', isActive: true, roles: ['farm_admin'] };
    authState.roles = ['farm_admin'];
    authState.grant = undefined;

    const { result } = renderHook(() => useSettingsSearchIndex({ enabled: false }), { wrapper });

    expect(result.current.isLoading).toBe(false);
    expect(result.current.isError).toBe(false);
  });
});
