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

vi.mock('@/features/admin/pages/SettingsPage', () => ({
  SettingsPage: ({
    allowedGroups,
    introText,
    afterContent,
  }: {
    allowedGroups?: string[];
    introText?: string;
    afterContent?: React.ReactNode;
  }) => (
    <div>
      <div
        data-testid="legacy-settings-page"
        data-groups={allowedGroups?.join(',') ?? 'all'}
        data-intro={introText ?? ''}
      >
        Legacy Settings Page
      </div>
      {afterContent}
    </div>
  ),
}));

vi.mock('@/features/settings/components/FarmSettingsSection', () => ({
  FarmSettingsSection: () => <div data-testid="farm-settings-section">Farm Settings Section</div>,
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

function renderSettings(initialRoute = '/settings') {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialRoute]}>
        <GlobalCommandPaletteProvider>
          <SettingsShell />
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
  vi.clearAllMocks();
});

describe('SettingsShell', () => {
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

  it('renders one flat nav listing every reachable category, with no scope switcher', () => {
    setAuthRoles(['farm_admin']);
    renderSettings();

    const h1s = screen.getAllByRole('heading', { level: 1, name: 'User Settings' });
    expect(h1s.length).toBeGreaterThanOrEqual(1);
    expect(screen.getByRole('heading', { level: 2, name: 'Profile' })).toBeInTheDocument();
    expect(screen.queryByText('Configure your farm, hardware, and account')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Search settings/i })).toBeInTheDocument();

    // Scope is a property of a category, not a mode to enter first: no radiogroup,
    // no separate Admin pill, one control idiom for the whole nav.
    expect(screen.queryByRole('radiogroup', { name: 'Settings scopes' })).not.toBeInTheDocument();
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();

    // Every destination an admin can reach is visible at once, grouped by scope.
    for (const label of ['User', 'System', 'Admin']) {
      expect(screen.getAllByRole('heading', { level: 2, name: label }).length).toBeGreaterThanOrEqual(1);
    }
    for (const label of ['Profile', 'General', 'Slicing', 'Hardware', 'Integrations', 'Quotas', 'Operations', 'Users', 'Data']) {
      expect(getCategoryButton(label)).toBeInTheDocument();
    }
    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
  });

  it('defaults to the User Settings profile category and preferences sub-page', () => {
    setAuthRoles(['farm_admin']);
    renderSettings();
    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Preferences' })).toHaveAttribute('aria-selected', 'true');
  });

  it('switches scope in one click by picking a category from another scope', () => {
    renderSettings();

    // Used to take two clicks: pick the System scope, then pick General.
    fireEvent.click(getCategoryButton('General'));

    expect(getCategoryButton('General')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('heading', { level: 2, name: 'General' })).toHaveFocus();
    expect(screen.getByTestId('legacy-settings-page')).toHaveAttribute('data-groups', 'General');
    expect(screen.getByTestId('farm-settings-section')).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=general');
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

  it('opens worker jobs from its canonical Admin URL', () => {
    renderSettings('/admin/manage?scope=admin&tab=operations&sub=workers&workerTab=jobs');

    expect(getCategoryButton('Operations')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Workers' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=admin');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=operations');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=workers');
    expect(screen.getByTestId('location-search')).toHaveTextContent('workerTab=jobs');
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

  it('redirects a non-admin away from admin deep links and explains the redirect', async () => {
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
    expect(toast.info).toHaveBeenCalledWith("You don't have access to admin settings. Showing your user settings instead.");
  });

  it('filters across scopes and lands on Slicing when searching for slicer', () => {
    renderSettings('/settings?q=slicer');

    expect(getCategoryButton(/^Slicing/)).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: /^Slicer Profiles/ })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=slicing');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=profiles');
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

  it('scopes the nav to farm-wide categories when the route locks the scope', () => {
    renderSettings('/settings?scope=system');

    expect(getCategoryButton('General')).toBeInTheDocument();
    expect(getCategoryButton('Slicing')).toBeInTheDocument();
    expect(getCategoryButton('Hardware')).toBeInTheDocument();
    expect(getCategoryButton('Integrations')).toBeInTheDocument();
    expect(getCategoryButton('Quotas')).toBeInTheDocument();

    // `?scope=system` on /settings is a soft preference, not a route lock — an
    // admin can still reach the other scopes from the same nav.
    expect(getCategoryButton('Profile')).toBeInTheDocument();
    expect(getCategoryButton('Operations')).toBeInTheDocument();
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

  it('opens Slicing on the defaults sub-page inside System Settings', () => {
    renderSettings('/settings?scope=system&tab=slicing');

    expect(getCategoryButton('Slicing')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Defaults' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('legacy-settings-page')).toHaveAttribute('data-groups', 'Slicing');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=slicing');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=defaults');
  });

  it('deep-links to hardware printer groups and renders the mapped page', () => {
    renderSettings('/settings?scope=system&tab=hardware&sub=printer-groups');
    expect(getCategoryButton('Hardware')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Printer Groups' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('printer-groups-page')).toHaveAttribute('data-embedded', 'true');
  });

  it('matches hardware sub-pages in search results', () => {
    renderSettings('/settings?q= binding ');
    expect(getCategoryButton(/^Hardware/)).toHaveAttribute('aria-current', 'page');
    expect(screen.queryByRole('tab', { name: 'Locations' })).not.toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /^NFC Bindings/ })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('nfc-bindings-page')).toHaveAttribute('data-embedded', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=hardware');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=nfc-bindings');
  });

  it('keeps category-level searches scoped to system settings', () => {
    renderSettings('/settings?q=hardware');
    expect(getCategoryButton('Hardware')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=hardware');
  });

  it('normalizes stale or incomplete system sub-page params to the rendered destination', () => {
    renderSettings('/settings?tab=hardware&sub=not-real');
    expect(getCategoryButton('Hardware')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=hardware');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=cameras');
  });

  it('shows a toast when a non-admin is redirected away from admin scope', async () => {
    setAuthRoles(['farm_user']);
    renderSettings('/settings?scope=admin');

    await waitFor(() => {
      expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
    });
    // The H1 names the scope, so this also proves the redirect landed on `user`
    // rather than merely rendering some settings page.
    expect(screen.getAllByRole('heading', { level: 1, name: 'User Settings' }).length).toBeGreaterThan(0);
    expect(screen.queryByText('Access Denied')).not.toBeInTheDocument();
    expect(toast.info).toHaveBeenCalledWith("You don't have access to admin settings. Showing your user settings instead.");
  });

  // The H1, the document title, and the sidebar's accessible name must all name
  // the same scope. They used to come from three places; one hardcoded string
  // drifted and shipped a page whose tab and heading disagreed.
  it.each([
    ['/settings?scope=user', 'User Settings'],
    ['/admin/settings?scope=system&tab=general', 'System Settings'],
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
    // Admin destinations live under /admin/manage — assert on pathname + query
    // rather than on the internal scope switcher, which is a separate concern
    // of the AdminManagePage wrapper.
    await waitFor(() => {
      expect(screen.getByTestId('location-pathname')).toHaveTextContent('/admin/manage');
    });
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=users');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=audit');
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
