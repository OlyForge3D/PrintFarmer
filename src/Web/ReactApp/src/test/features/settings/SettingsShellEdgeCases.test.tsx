import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SettingsShell } from '@/features/settings/pages/SettingsShell';
import { GlobalCommandPaletteProvider } from '@/features/settings/components/GlobalCommandPaletteProvider';

vi.mock('@/common/components/ThemeSwitcher', () => ({
  ThemeSwitcher: () => <div data-testid="theme-switcher">Theme Switcher</div>,
}));

vi.mock('@/features/printer-groups/pages/PrinterGroupsPage', () => ({
  PrinterGroupsPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="printer-groups-page" data-embedded={String(embedded)}>Printer Groups Page</div>,
}));

vi.mock('@/features/nfc/pages/NfcSettingsPage', () => ({
  NfcSettingsPage: () => <div data-testid="nfc-settings-page">NFC Readers and Bindings</div>,
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

vi.mock('@/features/admin/pages/SettingsPage', () => ({
  SettingsPage: ({
    pageKey,
    introText,
    afterContent,
  }: {
    pageKey?: string;
    introText?: string;
    afterContent?: React.ReactNode;
  }) => (
    <div>
      <div
        data-testid="legacy-settings-page"
        data-page-key={pageKey ?? ''}
        data-intro={introText ?? ''}
      >
        Legacy Settings Page
      </div>
      {afterContent}
    </div>
  ),
}));

const authState = {
  roles: ['farm_admin'],
};

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    user: { id: '1', email: authState.roles.includes('farm_admin') ? 'admin@test.com' : 'user@test.com', isActive: true, roles: authState.roles },
    isAuthenticated: true,
    isLoading: false,
    hasRole: (role: string) => authState.roles.includes(role),
    hasPermission: () => authState.roles.includes('farm_admin'),
    logout: vi.fn(),
  }),
  useAuthInternal: () => ({
    user: { id: '1', email: authState.roles.includes('farm_admin') ? 'admin@test.com' : 'user@test.com', isActive: true, roles: authState.roles },
    isAuthenticated: true,
    isLoading: false,
    hasRole: (role: string) => authState.roles.includes(role),
    hasPermission: () => authState.roles.includes('farm_admin'),
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

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: false } },
});

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location-search">{location.search}</div>;
}

function renderSettings(initialRoute = '/settings') {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialRoute]}>
        <GlobalCommandPaletteProvider>
          <SettingsShell routeScope={initialRoute.startsWith('/admin/settings') ? 'system' : undefined} />
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
});

describe('SettingsShell edge cases', () => {
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

  it('falls back to the single-page System category when the tab param is invalid', () => {
    renderSettings('/settings?scope=system&tab=not-real');

    expect(getCategoryButton('System')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('legacy-settings-page')).toHaveAttribute('data-page-key', 'system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=system');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('sub=');
  });

  it('does not fall back to personal content when an unprivileged user opens configuration', () => {
    setAuthRoles(['farm_user']);
    renderSettings('/admin/settings?scope=system&tab=users&sub=roles');
    expect(screen.getByText(/No settings editor is available/)).toBeInTheDocument();
    expect(screen.queryByTestId('theme-switcher')).not.toBeInTheDocument();
    expect(screen.queryByTestId('legacy-settings-page')).not.toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('tab=');
  });

  it('resolves unified configuration deep links that include both tab and sub params', () => {
    renderSettings('/admin/settings?scope=system&tab=users&sub=roles');

    expect(screen.getByRole('heading', { level: 1, name: 'Users' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Roles & Permissions' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByRole('navigation', { name: 'Settings categories' })).not.toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=users');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=roles');
  });
});
