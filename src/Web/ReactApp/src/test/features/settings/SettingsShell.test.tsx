import { beforeAll, describe, it, expect, vi } from 'vitest';
import { render, fireEvent, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SettingsShell } from '@/features/settings/pages/SettingsShell';

vi.mock('@/features/printer-groups/pages/PrinterGroupsPage', () => ({
  PrinterGroupsPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="printer-groups-page" data-embedded={String(embedded)}>Printer Groups Page</div>,
}));

vi.mock('@/features/nfc/pages/NfcBindingsPage', () => ({
  NfcBindingsPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="nfc-bindings-page" data-embedded={String(embedded)}>NFC Bindings Page</div>,
}));

vi.mock('@/features/profile/pages/ApiKeysPage', () => ({
  ApiKeysPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="api-keys-page" data-embedded={String(embedded)}>API Keys Page</div>,
}));

vi.mock('@/features/notifications/pages/NotificationPreferencesPage', () => ({
  NotificationPreferencesPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="notification-preferences-page" data-embedded={String(embedded)}>Notification Preferences Page</div>,
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    user: { id: '1', email: 'admin@test.com', isActive: true, roles: ['farm_admin'] },
    isAuthenticated: true,
    isLoading: false,
    hasRole: (role: string) => role === 'farm_admin',
    hasPermission: () => true,
    logout: vi.fn(),
  }),
  useAuthInternal: () => ({
    user: { id: '1', email: 'admin@test.com', isActive: true, roles: ['farm_admin'] },
    isAuthenticated: true,
    isLoading: false,
    hasRole: (role: string) => role === 'farm_admin',
    hasPermission: () => true,
    logout: vi.fn(),
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
        <SettingsShell />
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function getCategoryButton(name: string | RegExp) {
  return screen.getByRole('button', { name });
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

describe('SettingsShell', () => {
  it('renders the new scoped settings shell and admin-visible scope switcher', () => {
    renderSettings();

    const h1s = screen.getAllByRole('heading', { level: 1, name: 'Settings' });
    expect(h1s.length).toBeGreaterThanOrEqual(1);
    expect(screen.getByRole('heading', { level: 2, name: 'Profile' })).toBeInTheDocument();
    expect(screen.queryByText('Configure your farm, hardware, and account')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Command palette/i })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'User' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'System' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Admin/i })).toBeInTheDocument();
    expect(getCategoryButton('Profile')).toBeInTheDocument();
  });

  it('defaults to the User Settings profile category and preferences sub-page', () => {
    renderSettings();
    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Preferences' })).toHaveAttribute('aria-selected', 'true');
  });

  it('switches to System Settings from the scope switcher', () => {
    renderSettings();

    fireEvent.click(screen.getByRole('tab', { name: 'System' }));

    expect(getCategoryButton('General')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('heading', { level: 2, name: 'General' })).toHaveFocus();
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=general');
  });

  it('maps legacy notifications links into User Settings', () => {
    renderSettings('/settings?tab=notifications');

    expect(getCategoryButton('Profile')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Notifications' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('notification-preferences-page')).toHaveAttribute('data-embedded', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=profile');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=notifications');
  });

  it('renders search input', () => {
    renderSettings();
    expect(screen.getByRole('searchbox')).toBeInTheDocument();
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

  it('keeps category-level searches aligned with the rendered hardware sub-page', () => {
    renderSettings('/settings?q=hardware');
    expect(getCategoryButton(/^Hardware/)).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: /^Cameras/ })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: /^Locations/ })).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=hardware');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=cameras');

    fireEvent.click(getCategoryButton(/^Hardware/));
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=hardware');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=cameras');
  });

  it('normalizes stale or incomplete system sub-page params to the rendered destination', () => {
    renderSettings('/settings?tab=hardware&sub=not-real');
    expect(getCategoryButton('Hardware')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=hardware');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=cameras');
  });

  it('keeps API Keys reachable under User Settings through legacy links', () => {
    renderSettings('/settings?tab=users&sub=api-keys');
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

  it('updates search value from input and can land on single-page categories', () => {
    renderSettings();
    const searchInput = screen.getByLabelText('Search settings');
    searchInput.focus();
    fireEvent.change(searchInput, { target: { value: 'quota' } });
    expect(getCategoryButton(/^Quotas/)).toHaveAttribute('aria-current', 'page');
    expect(searchInput).toHaveFocus();
  });

  it('focuses search from the slash shortcut and clears the query', () => {
    renderSettings('/settings?q=hardware');

    fireEvent.keyDown(window, { key: '/' });
    const searchInput = screen.getByLabelText('Search settings');
    expect(searchInput).toHaveFocus();

    fireEvent.click(screen.getByRole('button', { name: 'Clear search' }));
    expect(searchInput).toHaveValue('');
  });

  it('opens the command palette from the header button and returns focus on escape', async () => {
    renderSettings();

    const launcher = screen.getByRole('button', { name: /Command palette/i });
    launcher.focus();
    fireEvent.click(launcher);

    const paletteSearch = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    expect(paletteSearch).toHaveFocus();

    fireEvent.keyDown(await screen.findByRole('dialog', { name: 'Command palette' }), { key: 'Escape' });
    expect(await screen.findByRole('button', { name: /Command palette/i })).toHaveFocus();
  });

  it('opens the command palette with Ctrl+K and navigates to a fuzzy-matched admin section', async () => {
    renderSettings();

    fireEvent.keyDown(window, { key: 'k', ctrlKey: true });
    const paletteSearch = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    fireEvent.change(paletteSearch, { target: { value: 'lgnadt' } });
    fireEvent.keyDown(paletteSearch, { key: 'ArrowDown' });
    fireEvent.keyDown(paletteSearch, { key: 'Enter' });

    expect(screen.queryByRole('dialog', { name: 'Command palette' })).not.toBeInTheDocument();
    expect(getCategoryButton('Users')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'Login Audit' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=admin');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=users');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=audit');
  });
});
