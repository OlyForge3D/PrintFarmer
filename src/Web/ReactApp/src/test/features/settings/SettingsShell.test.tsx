import { beforeAll, beforeEach, describe, it, expect, vi } from 'vitest';
import { render, fireEvent, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { toast } from 'sonner';
import { SettingsShell } from '@/features/settings/pages/SettingsShell';
import { SETTINGS_SCOPES } from '@/features/settings/types';
import { GlobalCommandPaletteProvider } from '@/features/settings/components/GlobalCommandPaletteProvider';

vi.mock('@/common/components/ThemeSwitcher', () => ({
  ThemeSwitcher: () => <div data-testid="theme-switcher">Theme Switcher</div>,
}));

vi.mock('@/features/printer-groups/pages/PrinterGroupsPage', () => ({
  PrinterGroupsPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="printer-groups-page" data-embedded={String(embedded)}>Printer Groups Page</div>,
}));

vi.mock('@/features/nfc/pages/NfcBindingsPage', () => ({
  NfcBindingsPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="nfc-bindings-page" data-embedded={String(embedded)}>NFC Bindings Page</div>,
}));

vi.mock('@/features/profile/pages/ApiKeysPage', () => ({
  ApiKeysPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="api-keys-page" data-embedded={String(embedded)}>API Keys Page</div>,
}));

vi.mock('@/features/profile/pages/PasskeysPage', () => ({
  PasskeysPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="passkeys-page" data-embedded={String(embedded)}>Passkeys Page</div>,
}));

vi.mock('@/features/notifications/pages/NotificationPreferencesPage', () => ({
  NotificationPreferencesPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="notification-preferences-page" data-embedded={String(embedded)}>Notification Preferences Page</div>,
}));

import { SettingsSaveRegistryContext } from '@/features/admin/settings/settingsSaveRegistry';
import React, { useContext } from 'react';

vi.mock('@/features/admin/pages/SettingsPage', () => ({
  SettingsPage: ({
    allowedGroups,
    introText,
    afterContent,
  }: {
    allowedGroups?: string[];
    introText?: string;
    afterContent?: React.ReactNode;
  }) => {
    const saveRegistry = useContext(SettingsSaveRegistryContext);
    return (
      <div>
        <div
          data-testid="legacy-settings-page"
          data-groups={allowedGroups?.join(',') ?? 'all'}
          data-intro={introText ?? ''}
        >
          Legacy Settings Page
        </div>
        <button
          data-testid="make-dirty-btn"
          onClick={() =>
            saveRegistry?.registerSection({
              id: 'test-section',
              name: 'Test Section',
              isDirty: true,
              onSave: async () => {},
            })
          }
        >
          Make Dirty
        </button>
        {afterContent}
      </div>
    );
  },
}));

vi.mock('@/features/settings/components/IntegrationSettingsCards', () => ({
  SpoolmanSettingsCard: () => <div data-testid="spoolman-settings">Spoolman settings</div>,
  HomeAssistantSettingsCard: () => <div data-testid="home-assistant-settings">Home Assistant settings</div>,
}));
vi.mock('@/features/settings/components/TelegramSettingsCard', () => ({
  TelegramSettingsCard: () => <div data-testid="telegram-settings">Telegram settings</div>,
}));
vi.mock('@/features/admin/pages/UserManagementPage', () => ({
  UserManagementPage: () => <div data-testid="accounts-editor">Accounts editor</div>,
}));
vi.mock('@/features/admin/pages/RoleManagementPage', () => ({
  RoleManagementPage: () => <div data-testid="roles-editor">Roles editor</div>,
}));
vi.mock('@/features/admin/pages/TagAdminPage', () => ({
  TagAdminPage: () => <div data-testid="tags-editor">Tags editor</div>,
}));
vi.mock('@/features/quotas/pages/QuotaManagementPage', () => ({
  QuotaManagementPage: () => <div data-testid="quotas-editor">Quotas editor</div>,
}));
vi.mock('@/features/settings/components/FarmSettingsSection', () => ({
  FarmSettingsSection: () => <div data-testid="farm-settings-section">Farm Settings Section</div>,
}));

const authState: { roles: string[]; permissionOverride: ((resource: string, action: string) => boolean) | null } = {
  roles: ['farm_admin'],
  // #1457: lets a test simulate a custom (non-farm_admin) role granted one
  // specific admin permission, independent of the role-shaped default below,
  // so per-tab gating (`canAccessDestination`) can be proven against a real
  // custom-role scenario rather than only farm_admin vs. nothing.
  permissionOverride: null,
};

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    user: { id: '1', email: authState.roles.includes('farm_admin') ? 'admin@test.com' : 'user@test.com', isActive: true, roles: authState.roles },
    isAuthenticated: true,
    isLoading: false,
    hasRole: (role: string) => authState.roles.includes(role),
    hasPermission: (resource: string, action: string) =>
      authState.permissionOverride ? authState.permissionOverride(resource, action) : authState.roles.includes('farm_admin'),
    logout: vi.fn(),
  }),
  useAuthInternal: () => ({
    user: { id: '1', email: authState.roles.includes('farm_admin') ? 'admin@test.com' : 'user@test.com', isActive: true, roles: authState.roles },
    isAuthenticated: true,
    isLoading: false,
    hasRole: (role: string) => authState.roles.includes(role),
    hasPermission: (resource: string, action: string) =>
      authState.permissionOverride ? authState.permissionOverride(resource, action) : authState.roles.includes('farm_admin'),
    logout: vi.fn(),
  }),
}));

vi.mock('@/common/hooks/useTheme', () => ({
  useTheme: () => ({
    theme: 'dark',
    setTheme: vi.fn(),
    themes: ['light', 'dark'],
    isLight: false,
    isDark: true,
  }),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true }),
}));

vi.mock('sonner', () => ({
  toast: {
    info: vi.fn(),
    error: vi.fn(),
    success: vi.fn(),
  },
}));

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: false } },
});

function LocationProbe() {
  const location = useLocation();
  return (
    <>
      <div data-testid="location-search">{location.search}</div>
      <div data-testid="location-pathname">{location.pathname}</div>
    </>
  );
}

function renderSettings(initialRoute = '/settings', routeScope: 'user' | 'system' | undefined = initialRoute.startsWith('/admin/settings') ? 'system' : undefined) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialRoute]}>
        <GlobalCommandPaletteProvider>
          <SettingsShell routeScope={routeScope} />
        </GlobalCommandPaletteProvider>
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function getCategoryButton(name: string | RegExp) {
  const matches = screen.getAllByRole('button', { name });
  return matches.find((button) => button.getAttribute('aria-current') === 'page') ?? matches[0];
}

function setAuthRoles(roles: string[]) {
  authState.roles = roles;
}

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation(() => ({
      matches: false,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    })),
  });
});

beforeEach(() => {
  setAuthRoles(['farm_admin']);
  authState.permissionOverride = null;
  vi.clearAllMocks();
});

describe('SettingsShell', () => {
  it.each([
    ['system_settings', 'general', 'farm', 'legacy-settings-page'],
    ['users', 'users', 'accounts', 'accounts-editor'],
    ['roles', 'users', 'roles', 'roles-editor'],
    ['tags', 'data', 'tags', 'tags-editor'],
    ['printers', 'hardware', 'printer-groups', 'printer-groups-page'],
    ['quota', 'quotas', '', 'quotas-editor'],
  ])('selects the first accessible editor for a %s delegate on bare, unknown, and category URLs', (resource, category, sub, editor) => {
    setAuthRoles(['farm_user']);
    authState.permissionOverride = (r, action) => r === resource && action === 'admin';
    for (const path of ['/admin/settings', '/admin/settings?tab=unknown', `/admin/settings?tab=${category}`, `/admin/settings?tab=${category}&sub=unknown`]) {
      const mounted = renderSettings(path);
      expect(screen.getByTestId(editor)).toBeInTheDocument();
      expect(screen.queryByTestId('theme-switcher')).not.toBeInTheDocument();
      expect(screen.getByTestId('location-search')).toHaveTextContent(`tab=${category}`);
      if (sub) expect(screen.getByTestId('location-search')).toHaveTextContent(`sub=${sub}`);
      mounted.unmount();
    }
  });

  it.each([
    ['power_monitors', 'Power Monitors', '/admin/power-monitors'],
    ['locations', 'Locations', '/locations'],
    ['catalog', 'Catalog', '/catalog'],
  ])('keeps a standalone-only %s delegate on useful links without an editor', (resource, label, path) => {
    setAuthRoles(['farm_user']);
    authState.permissionOverride = (r, action) => r === resource && action === 'admin';
    renderSettings('/admin/settings?scope=user&tab=profile&sub=preferences&field=SystemLog.Enabled');
    expect(screen.getByRole('link', { name: label })).toHaveAttribute('href', path);
    expect(screen.getByText(/No settings editor is available/)).toBeInTheDocument();
    expect(screen.queryByTestId('legacy-settings-page')).not.toBeInTheDocument();
    expect(screen.queryByTestId('theme-switcher')).not.toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('tab=');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('sub=');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('field=');
  });

  it('never falls back to personal content for a user with no admin grants', () => {
    setAuthRoles(['farm_user']);
    renderSettings('/admin/settings');
    expect(screen.getByText(/No settings editor is available/)).toBeInTheDocument();
    expect(screen.queryByTestId('legacy-settings-page')).not.toBeInTheDocument();
    expect(screen.queryByTestId('theme-switcher')).not.toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Standalone configuration' })).not.toBeInTheDocument();
  });

  it.each([
    ['spoolman', 'spoolman-settings'],
    ['home_assistant', 'home-assistant-settings'],
    ['telegram', 'telegram-settings'],
  ])('mounts only the authorized service for an integration %s delegate', (resource, editor) => {
    setAuthRoles(['farm_user']);
    authState.permissionOverride = (r, action) => r === resource && action === 'admin';
    renderSettings('/admin/settings?tab=integrations&sub=connections');
    expect(screen.getByTestId(editor)).toBeInTheDocument();
    for (const id of ['spoolman-settings', 'home-assistant-settings', 'telegram-settings', 'legacy-settings-page']) {
      if (id !== editor) expect(screen.queryByTestId(id)).not.toBeInTheDocument();
    }
  });

  it.each([true, false])('uses one Spoolman editor when metadata editing is allowed (farm administrator: %s)', (isFarmAdmin) => {
    if (!isFarmAdmin) {
      setAuthRoles(['farm_user']);
      authState.permissionOverride = (resource, action) =>
        ['system_settings', 'spoolman'].includes(resource) && action === 'admin';
    }
    renderSettings('/admin/settings?tab=integrations&sub=connections');
    expect(screen.getAllByTestId('legacy-settings-page')).toHaveLength(1);
    expect(screen.getByTestId('legacy-settings-page')).toHaveAttribute('data-groups', 'Integrations');
    expect(screen.queryByTestId('spoolman-settings')).not.toBeInTheDocument();
  });

  it('keeps personal settings route-locked even for a farm administrator', () => {
    renderSettings('/settings?scope=system&tab=users&sub=accounts', 'user');
    expect(screen.getByTestId('theme-switcher')).toBeInTheDocument();
    expect(screen.queryByTestId('accounts-editor')).not.toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
  });

  it('does not grant the role-only profile library to resource delegates', () => {
    setAuthRoles(['farm_user']);
    authState.permissionOverride = () => true;
    renderSettings('/admin/settings?tab=slicing&sub=profiles');
    expect(screen.getByText(/don't have permission to view/)).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Slicer Profiles' })).not.toBeInTheDocument();
  });

  it('shows only the User scope to non-admins and keeps /settings on personal pages', () => {
    setAuthRoles(['farm_user']);
    renderSettings('/settings?scope=system&tab=hardware');

    expect(screen.queryByRole('radio', { name: 'System' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Admin/i })).not.toBeInTheDocument();
    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Preferences' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=profile');
  });

  it('renders user settings on /settings and grouped system destinations on /admin/settings', () => {
    setAuthRoles(['farm_admin']);
    renderSettings();

    const h1s = screen.getAllByRole('heading', { level: 1, name: 'User Settings' });
    expect(h1s.length).toBeGreaterThanOrEqual(1);
    expect(screen.getByRole('heading', { level: 2, name: 'Profile' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Search settings/i })).toBeInTheDocument();

    // Scope is a property of a category, not a mode to enter first: no radiogroup,
    // no separate Admin pill, one control idiom for the whole nav.
    expect(screen.queryByRole('radiogroup', { name: 'Settings scopes' })).not.toBeInTheDocument();
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();

    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
  });

  it('renders 8 display groups and direct-leaf destinations on /admin/settings', () => {
    setAuthRoles(['farm_admin']);
    renderSettings('/admin/settings?scope=system');

    for (const groupLabel of [
      'Farm',
      'Printing & slicing',
      'Hardware',
      'Automation & costs',
      'Integrations',
      'People & access',
      'Organization',
      'System',
    ]) {
      expect(screen.getByText(groupLabel)).toBeInTheDocument();
    }

    for (const destLabel of [
      'Farm Defaults',
      'Quotas',
      'Slicer Defaults',
      'Printer Groups',
      'Automation & Costs',
      'User Accounts',
      'System Config',
    ]) {
      expect(getCategoryButton(destLabel)).toBeInTheDocument();
    }
    expect(getCategoryButton('Farm Defaults')).toHaveAttribute('aria-current', 'page');
  });

  it('defaults to the User Settings profile category and preferences sub-page', () => {
    setAuthRoles(['farm_admin']);
    renderSettings();
    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Preferences' })).toHaveAttribute('aria-selected', 'true');
  });

  it('switches to system scope when picking a system destination', () => {
    renderSettings('/admin/settings?scope=system&tab=farm');

    expect(getCategoryButton('Farm Defaults')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('legacy-settings-page')).toHaveAttribute('data-groups', 'General');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=general');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=farm');
  });

  it('opens Notifications from its canonical User Settings URL', () => {
    renderSettings('/settings?scope=user&tab=profile&sub=notifications');

    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Notifications' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('notification-preferences-page')).toHaveAttribute('data-embedded', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=profile');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=notifications');
  });

  it('opens accounts inside the single-pane configuration workspace', () => {
    renderSettings('/admin/settings?scope=system&tab=users');
    expect(getCategoryButton('User Accounts')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('accounts-editor')).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.queryByRole('tab')).not.toBeInTheDocument();
  });

  it('renders passkeys from the existing profile page inside User Settings', () => {
    setAuthRoles(['farm_admin']);
    renderSettings('/settings?scope=user&tab=profile&sub=passkeys');

    expect(screen.getByRole('tab', { name: 'Passkeys' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('passkeys-page')).toHaveAttribute('data-embedded', 'true');
  });

  it('falls back to the User scope when the scope param is invalid', () => {
    renderSettings('/settings?scope=not-a-real-scope');

    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Preferences' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('scope=not-a-real-scope');
  });

  it('defaults an empty scope param back to User Settings', () => {
    renderSettings('/settings?scope=');

    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Preferences' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('scope=&');
  });

  it('keeps an unprivileged personal request in user settings', async () => {
    setAuthRoles(['farm_user']);
    renderSettings('/settings?scope=admin&tab=users&sub=audit');

    await waitFor(() => {
      expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
    });
    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Preferences' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('scope=admin');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('tab=users');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('sub=audit');
    expect(toast.info).not.toHaveBeenCalled();
  });

  it('filters destinations and lands on Slicing & profiles when searching for slicer on admin settings', () => {
    renderSettings('/admin/settings?q=slicer');

    expect(getCategoryButton('Slicer Defaults')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=slicing');
  });

  it('shows empty state when no categories match search', () => {
    renderSettings('/settings?q=xyznonexistent');
    expect(screen.getByText('No matching settings')).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('tab=');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('sub=');
  });

  it('clears stale tab and sub params when search returns no results', () => {
    renderSettings('/settings?scope=system&tab=hardware&sub=cameras&q=xyznonexistent');
    expect(screen.getByText('No matching settings')).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('tab=');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('sub=');
  });

  it('renders system display groups and direct-leaf destinations on /admin/settings?scope=system', () => {
    renderSettings('/admin/settings?scope=system');

    expect(getCategoryButton('Farm Defaults')).toBeInTheDocument();
    expect(getCategoryButton('Slicer Defaults')).toBeInTheDocument();
    expect(getCategoryButton('Printer Groups')).toBeInTheDocument();
    expect(getCategoryButton('Automation & Costs')).toBeInTheDocument();
    expect(getCategoryButton('User Accounts')).toBeInTheDocument();
  });

  it('hides scopes the user cannot reach', () => {
    setAuthRoles(['farm_user']);
    renderSettings('/settings');

    expect(getCategoryButton('Profile')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'General' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Operations' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Users' })).not.toBeInTheDocument();
    // A single group needs no caption; the page title already says "Settings".
    expect(screen.queryByRole('heading', { level: 2, name: 'User' })).not.toBeInTheDocument();
  });

  it('opens Slicing & profiles destination inside System Settings', () => {
    renderSettings('/admin/settings?scope=system&tab=slicing');

    expect(getCategoryButton('Slicer Defaults')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('legacy-settings-page')).toHaveAttribute('data-groups', 'Slicing');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=slicing');
  });

  it('deep-links to printers under hardware display group and renders the mapped page', () => {
    renderSettings('/admin/settings?scope=system&tab=printers');
    expect(getCategoryButton('Printer Groups')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('printer-groups-page')).toBeInTheDocument();
  });

  it('lets a custom (non-farm_admin) role granted only printers:admin reach system scope and the printers destination', () => {
    setAuthRoles(['farm_user']);
    authState.permissionOverride = (resource, action) => resource === 'printers' && action === 'admin';
    renderSettings('/admin/settings?scope=system&tab=printers');

    expect(getCategoryButton('Printer Groups')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('printer-groups-page')).toBeInTheDocument();
    expect(screen.queryByText(/don't have permission to view/i)).not.toBeInTheDocument();
  });

  it('denies a custom (non-farm_admin) role a specific tab it lacks the requiredPermission for', () => {
    setAuthRoles(['farm_user']);
    authState.permissionOverride = (resource, action) => resource === 'printers' && action === 'admin';
    renderSettings('/admin/settings?scope=system&tab=hardware&sub=cameras');

    expect(screen.getByText(/don't have permission to view/i)).toBeInTheDocument();
    expect(screen.queryByTestId('printer-groups-page')).not.toBeInTheDocument();
  });

  it('hides sidebar destinations the user cannot access, showing only unlocked ones', () => {
    setAuthRoles(['farm_user']);
    authState.permissionOverride = (resource, action) => resource === 'printers' && action === 'admin';
    renderSettings('/admin/settings?scope=system&tab=printers');

    expect(getCategoryButton('Printer Groups')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Home Assistant' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Telegram' })).not.toBeInTheDocument();
  });

  it('lands a partial-permission user on their first reachable destination on a bare URL', () => {
    setAuthRoles(['farm_user']);
    authState.permissionOverride = (resource, action) => resource === 'printers' && action === 'admin';
    renderSettings('/admin/settings?scope=system');

    expect(getCategoryButton('Printer Groups')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('printer-groups-page')).toBeInTheDocument();
    expect(screen.queryByText(/don't have permission to view/i)).not.toBeInTheDocument();
  });

  it('shows Integrations destinations once the user holds any required permission', () => {
    setAuthRoles(['farm_user']);
    authState.permissionOverride = (resource, action) => resource === 'telegram' && action === 'admin';
    renderSettings('/admin/settings?scope=system&tab=telegram');

    expect(getCategoryButton('External Services')).toHaveAttribute('aria-current', 'page');
    expect(screen.queryByText(/don't have permission to view/i)).not.toBeInTheDocument();
  });

  it('matches hardware destinations in search results', () => {
    renderSettings('/admin/settings?q=Printers');
    expect(getCategoryButton('Printer Groups')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
  });

  it('keeps destination searches scoped to system settings', () => {
    renderSettings('/admin/settings?q=hardware');
    expect(getCategoryButton('Printer Groups')).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
  });

  it('renders direct-leaf destination for system scope', () => {
    renderSettings('/admin/settings?scope=system&tab=users');
    expect(getCategoryButton('User Accounts')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=users');
  });

  it('normalizes an invalid scope on personal settings', async () => {
    setAuthRoles(['farm_user']);
    renderSettings('/settings?scope=admin');

    await waitFor(() => {
      expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
    });
    // The H1 names the scope, so this also proves the redirect landed on `user`
    // rather than merely rendering some settings page.
    expect(screen.getAllByRole('heading', { level: 1, name: 'User Settings' }).length).toBeGreaterThan(0);
    expect(screen.queryByText('Access Denied')).not.toBeInTheDocument();
    expect(toast.info).not.toHaveBeenCalled();
  });

  // The H1, the document title, and the sidebar's accessible name must all name
  // the same scope. They used to come from three places; one hardcoded string
  // drifted and shipped a page whose tab and heading disagreed.
  it.each([
    ['/settings?scope=user', 'User Settings'],
    ['/admin/settings?scope=system&tab=general', 'Farm & Admin Settings'],
  ])('titles %s from the scope registry, not a hardcoded string', (route, expected) => {
    setAuthRoles(['farm_admin']);
    renderSettings(route);

    expect(screen.getAllByRole('heading', { level: 1, name: expected }).length).toBeGreaterThan(0);
    expect(SETTINGS_SCOPES.some(scope => scope.label === expected)).toBe(true);
  });

  it('opens API Keys from its canonical User Settings URL', () => {
    renderSettings('/settings?scope=user&tab=profile&sub=api-keys');
    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'API Keys' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('api-keys-page')).toHaveAttribute('data-embedded', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=profile');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=api-keys');
  });

  it('prefers direct sub-page matches over earlier category keyword matches', () => {
    renderSettings('/settings?q=api');
    expect(getCategoryButton(/^Profile/)).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: /^API Keys/ })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=profile');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=api-keys');
  });

  it('opens the command palette from the header button and returns focus on escape', async () => {
    renderSettings();

    const launcher = screen.getByRole('button', { name: /Search settings/i });
    launcher.focus();
    fireEvent.click(launcher);

    const paletteSearch = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    // #1028: `findBy*` resolves as soon as the node exists, but the palette
    // moves focus in an effect that lands a tick later. Asserting focus
    // immediately loses that race under load.
    await waitFor(() => expect(paletteSearch).toHaveFocus());

    fireEvent.keyDown(await screen.findByRole('dialog', { name: 'Command palette' }), { key: 'Escape' });
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /Search settings/i })).toHaveFocus()
    );
  });

  it('opens the command palette with Ctrl+K and navigates to a fuzzy-matched admin destination', async () => {
    renderSettings();

    fireEvent.keyDown(window, { key: 'k', ctrlKey: true });
    const paletteSearch = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    // "login audit" targets the admin destination surfaced by the #934 registry.
    fireEvent.change(paletteSearch, { target: { value: 'login audit' } });
    fireEvent.keyDown(paletteSearch, { key: 'ArrowDown' });
    fireEvent.keyDown(paletteSearch, { key: 'Enter' });

    expect(screen.queryByRole('dialog', { name: 'Command palette' })).not.toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByTestId('location-pathname')).toHaveTextContent('/admin/login-audit');
    });
  });

  it('intercepts navigation with Stay/Discard decision modal when a section is dirty', () => {
    setAuthRoles(['farm_admin']);
    renderSettings('/admin/settings?scope=system');

    // Currently on Farm Defaults
    expect(getCategoryButton('Farm Defaults')).toHaveAttribute('aria-current', 'page');

    // Register a dirty section via mock button
    fireEvent.click(screen.getByTestId('make-dirty-btn'));

    // Attempt to navigate to Quotas
    fireEvent.click(getCategoryButton('Quotas'));

    // Modal opens asking whether to stay or discard
    expect(screen.getByRole('dialog', { name: 'Unsaved Changes' })).toBeInTheDocument();
    expect(screen.getByText(/You have unsaved changes/i)).toBeInTheDocument();

    // Click Stay
    fireEvent.click(screen.getByRole('button', { name: 'Stay' }));

    // Modal closes and user remains on Farm Defaults
    expect(screen.queryByRole('dialog', { name: 'Unsaved Changes' })).not.toBeInTheDocument();
    expect(getCategoryButton('Farm Defaults')).toHaveAttribute('aria-current', 'page');

    // Attempt to navigate to Quotas again
    fireEvent.click(getCategoryButton('Quotas'));
    expect(screen.getByRole('dialog', { name: 'Unsaved Changes' })).toBeInTheDocument();

    // Click Discard Changes
    fireEvent.click(screen.getByRole('button', { name: 'Discard Changes' }));

    // Navigation proceeds to Quotas
    expect(screen.queryByRole('dialog', { name: 'Unsaved Changes' })).not.toBeInTheDocument();
    expect(getCategoryButton('Quotas')).toHaveAttribute('aria-current', 'page');
  });

  it('opens mobile navigation selector and restores focus on Escape key', async () => {
    setAuthRoles(['farm_admin']);
    renderSettings('/admin/settings?scope=system');

    const mobileToggle = screen.getByRole('button', { name: /Settings section: Farm Defaults/i });
    expect(mobileToggle).toHaveAttribute('aria-expanded', 'false');

    mobileToggle.focus();
    fireEvent.click(mobileToggle);

    expect(mobileToggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('navigation', { name: 'Settings categories' })).toBeInTheDocument();

    // Press Escape to close mobile selector and restore focus
    fireEvent.keyDown(document, { key: 'Escape' });

    expect(mobileToggle).toHaveAttribute('aria-expanded', 'false');
    await waitFor(() => expect(mobileToggle).toHaveFocus());
  });
});

describe('SettingsShell — footer slot sits below the scrollport (Vasquez #1)', () => {
  /**
   * The save bar has to be reachable while the cards above it scroll. It used
   * to try to achieve that with `position: sticky` from inside the scroll pane,
   * which cannot work: a sticky box is bound by the scrolled content, so it
   * flowed off the bottom of the page instead of pinning. The shell now exposes
   * a slot *below* the pane and the content page portals its bar into it.
   *
   * If the slot ever moves back inside `.pf-settings-scroll-pane`, the bar goes
   * back under the fold and this fails.
   */
  it('renders the footer slot outside the scroll pane', () => {
    const { container } = renderSettings('/settings');

    const pane = container.querySelector('.pf-settings-scroll-pane');
    expect(pane).not.toBeNull();

    const slot = container.querySelector('.shrink-0.empty\\:hidden');
    expect(slot).not.toBeNull();
    expect(pane!.contains(slot!)).toBe(false);
  });

  /** The pane must stay the scrollport, and the slot must follow it. */
  it('orders the slot after the pane inside the same column', () => {
    const { container } = renderSettings('/settings');

    const pane = container.querySelector('.pf-settings-scroll-pane')!;
    const slot = container.querySelector('.shrink-0.empty\\:hidden')!;

    expect(slot.parentElement).toBe(pane.parentElement);
    expect(
      pane.compareDocumentPosition(slot) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });
});
