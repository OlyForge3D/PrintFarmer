/**
 * Behaviour tests for the global command-palette provider (#938).
 *
 * The provider owns the Ctrl+K listener, palette state, item assembly, and
 * routing. These tests use the real palette component under a mocked router +
 * auth surface so we exercise both the item wiring and the selection routing.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { GlobalCommandPaletteProvider } from '@/features/settings/components/GlobalCommandPaletteProvider';
import { useCommandPalette } from '@/features/settings/components/commandPaletteContext';

const authState: {
  roles: string[];
  grant?: string;
  logout: ReturnType<typeof vi.fn>;
} = {
  roles: ['farm_admin'],
  logout: vi.fn(),
};

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    user: { id: '1', email: 'admin@test.com', isActive: true, roles: authState.roles },
    isAuthenticated: true,
    isLoading: false,
    hasRole: (role: string) => authState.roles.includes(role),
    hasPermission: (resource: string, action: string) => authState.roles.includes('farm_admin')
      || authState.grant === `${resource}:${action}`,
    logout: authState.logout,
  }),
}));

vi.mock('@/features/settings/queries/useSettingsMetadata', () => ({
  useSettingsMetadata: () => ({ data: [{
    key: 'SystemLog', className: 'SystemLogSettings', group: 'System',
    properties: [{ name: 'Enabled', type: 'boolean', display: { name: 'Enable System Logging' } }],
  }] }),
  useSettingsGroups: () => ({ data: [{ key: 'System', displayName: 'System', order: 0 }] }),
}));

const setThemeMock = vi.fn();
vi.mock('@/common/hooks/useTheme', () => ({
  useTheme: () => ({
    theme: 'dark',
    setTheme: setThemeMock,
    themes: ['light', 'dark'],
    isLight: false,
    isDark: true,
  }),
}));

vi.mock('sonner', () => ({
  toast: {
    info: vi.fn(),
    error: vi.fn(),
    success: vi.fn(),
  },
}));

function LocationProbe() {
  const location = useLocation();
  return (
    <div>
      <div data-testid="pathname">{location.pathname}</div>
      <div data-testid="search">{location.search}</div>
    </div>
  );
}

function OpenerButton() {
  const { open } = useCommandPalette();
  return <button type="button" onClick={open}>Open palette</button>;
}

function renderProvider() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/']}>
        <GlobalCommandPaletteProvider>
          <OpenerButton />
          <LocationProbe />
        </GlobalCommandPaletteProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('GlobalCommandPaletteProvider', () => {
  beforeEach(() => {
    authState.grant = undefined;
    authState.roles = ['farm_admin'];
    authState.logout.mockClear();
    setThemeMock.mockClear();
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: vi.fn().mockImplementation(() => ({
        matches: false,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    });
  });

  it('opens with Ctrl+K from anywhere on the page', async () => {
    renderProvider();

    fireEvent.keyDown(window, { key: 'k', ctrlKey: true });

    expect(await screen.findByRole('dialog', { name: 'Command palette' })).toBeInTheDocument();
  });

  it('gives a system-settings delegate a section-qualified field destination', async () => {
    authState.roles = ['farm_user'];
    authState.grant = 'system_settings:admin';
    renderProvider();
    fireEvent.click(screen.getByRole('button', { name: 'Open palette' }));
    const input = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    fireEvent.change(input, { target: { value: 'Enable System Logging' } });
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(screen.getByTestId('pathname')).toHaveTextContent('/admin/settings'));
    expect(screen.getByTestId('search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('search')).toHaveTextContent('field=SystemLog.Enabled');
  });

  it('routes admin destination selection to the destination href', async () => {
    renderProvider();

    fireEvent.click(screen.getByRole('button', { name: 'Open palette' }));
    const input = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    fireEvent.change(input, { target: { value: 'login audit' } });
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(screen.getByTestId('pathname')).toHaveTextContent('/admin/login-audit');
    });
    expect(screen.getByTestId('search')).toHaveTextContent('');
  });

  it('runs the switch-theme action inline and closes the palette', async () => {
    renderProvider();

    fireEvent.click(screen.getByRole('button', { name: 'Open palette' }));
    const input = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    fireEvent.change(input, { target: { value: 'switch to light' } });
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(setThemeMock).toHaveBeenCalledWith('light');
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Command palette' })).not.toBeInTheDocument();
    });
  });

  it('prompts before running a destructive action and cancels on decline', async () => {
    renderProvider();

    fireEvent.click(screen.getByRole('button', { name: 'Open palette' }));
    const input = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    fireEvent.change(input, { target: { value: 'sign out' } });
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });

    // An in-app modal, not window.confirm.
    expect(await screen.findByText('Sign out of PrintFarmer?')).toBeInTheDocument();
    expect(authState.logout).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }));

    await waitFor(() => {
      expect(screen.queryByText('Sign out of PrintFarmer?')).not.toBeInTheDocument();
    });
    expect(authState.logout).not.toHaveBeenCalled();
  });

  it('runs the destructive action when the user confirms', async () => {
    renderProvider();

    fireEvent.click(screen.getByRole('button', { name: 'Open palette' }));
    const input = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    fireEvent.change(input, { target: { value: 'sign out' } });
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(await screen.findByText('Sign out of PrintFarmer?')).toBeInTheDocument();
    expect(authState.logout).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));

    await waitFor(() => {
      expect(authState.logout).toHaveBeenCalled();
    });
  });

  it('throws when useCommandPalette is used outside the provider', () => {
    function Consumer() {
      useCommandPalette();
      return null;
    }
    // React logs the boundary error via console.error — silence to avoid noisy
    // test output; the throw itself is what we're asserting.
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    expect(() => render(<Consumer />)).toThrow(/useCommandPalette must be used inside/);
    errorSpy.mockRestore();
  });
});
