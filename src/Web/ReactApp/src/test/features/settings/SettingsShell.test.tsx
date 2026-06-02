import { describe, it, expect, vi } from 'vitest';
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

// Mock auth context
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    user: { id: '1', email: 'admin@test.com', isActive: true, roles: ['farm_admin'] },
    isAuthenticated: true,
    isLoading: false,
    hasRole: () => true,
    hasPermission: () => true,
    logout: vi.fn(),
  }),
  useAuthInternal: () => ({
    user: { id: '1', email: 'admin@test.com', isActive: true, roles: ['farm_admin'] },
    isAuthenticated: true,
    isLoading: false,
    hasRole: () => true,
    hasPermission: () => true,
    logout: vi.fn(),
  }),
}));

// Mock slicer context
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
    </QueryClientProvider>
  );
}

function getCategoryButton(name: string | RegExp) {
  return screen.getByRole('button', { name });
}

describe('SettingsShell', () => {
  it('renders the settings heading and category tabs', () => {
    renderSettings();
    expect(screen.getByRole('heading', { level: 1, name: 'Settings' })).toBeInTheDocument();
    expect(getCategoryButton('General')).toBeInTheDocument();
    expect(getCategoryButton('Hardware')).toBeInTheDocument();
    expect(getCategoryButton('Users')).toBeInTheDocument();
  });

  it('defaults to the General tab', () => {
    renderSettings();
    const generalCategory = getCategoryButton('General');
    expect(generalCategory).toHaveAttribute('aria-current', 'page');
    expect(generalCategory.className).toContain('text-[var(--pf-on-accent)]');
  });

  it('switches tab on click', () => {
    renderSettings();
    const hardwareCategory = getCategoryButton('Hardware');
    fireEvent.click(hardwareCategory);
    expect(hardwareCategory).toHaveAttribute('aria-current', 'page');
    expect(getCategoryButton('General')).not.toHaveAttribute('aria-current', 'page');
  });

  it('deep-links to a specific tab via URL', () => {
    renderSettings('/settings?tab=notifications');
    const notificationsCategory = getCategoryButton('Notifications');
    expect(notificationsCategory).toHaveAttribute('aria-current', 'page');
  });

  it('renders search input', () => {
    renderSettings();
    expect(screen.getByRole('searchbox')).toBeInTheDocument();
  });

  it('filters tabs by search query', () => {
    renderSettings('/settings?q=slicer');
    expect(getCategoryButton(/^Slicing/)).toHaveAttribute('aria-current', 'page');
    expect(screen.queryByRole('button', { name: 'Users' })).not.toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /^Slicer Profiles/ })).toHaveAttribute('aria-selected', 'true');
  });

  it('shows empty state when no tabs match search', () => {
    renderSettings('/settings?q=xyznonexistent');
    expect(screen.getByText(/No settings found matching/)).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('tab=');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('sub=');
  });

  it('clears stale tab and sub params when search returns no results', () => {
    renderSettings('/settings?tab=hardware&sub=cameras&q=xyznonexistent');
    expect(screen.getByText(/No settings found matching/)).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('tab=');
    expect(screen.getByTestId('location-search')).not.toHaveTextContent('sub=');
  });

  it('deep-links to hardware printer groups and renders the mapped page', () => {
    renderSettings('/settings?tab=hardware&sub=printer-groups');
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
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=hardware');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=nfc-bindings');
  });

  it('keeps category-level searches aligned with the rendered hardware sub-page', () => {
    renderSettings('/settings?q=hardware');
    expect(getCategoryButton(/^Hardware/)).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: /^Cameras/ })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: /^Locations/ })).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=hardware');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=cameras');

    fireEvent.click(getCategoryButton(/^Hardware/));
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=hardware');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=cameras');
  });

  it('normalizes stale or incomplete sub-page params to the rendered destination', () => {
    renderSettings('/settings?tab=hardware&sub=not-real');
    expect(getCategoryButton('Hardware')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=hardware');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=cameras');
  });

  it('keeps API Keys reachable under Users settings', () => {
    renderSettings('/settings?tab=users&sub=api-keys');
    expect(getCategoryButton('Users')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: 'API Keys' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('api-keys-page')).toHaveAttribute('data-embedded', 'true');
  });

  it('prefers direct sub-page matches over earlier category keyword matches', () => {
    renderSettings('/settings?q=api');
    expect(getCategoryButton(/^Users/)).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('tab', { name: /^API Keys/ })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=users');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=api-keys');
  });

  it('updates search value from input', () => {
    renderSettings();
    const searchInput = screen.getByLabelText('Search settings');
    searchInput.focus();
    fireEvent.change(searchInput, { target: { value: 'email' } });
    // After typing, the notifications category should match and become active.
    expect(getCategoryButton(/^Notifications/)).toHaveAttribute('aria-current', 'page');
    expect(searchInput).toHaveFocus();
  });
});
