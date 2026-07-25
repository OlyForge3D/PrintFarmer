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
    hasPermission: () => authState.roles.includes('farm_admin'),
    logout: authState.logout,
  }),
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

  it('routes admin destination selection to the destination href', async () => {
    renderProvider();

    fireEvent.click(screen.getByRole('button', { name: 'Open palette' }));
    const input = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    fireEvent.change(input, { target: { value: 'login audit' } });
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(screen.getByTestId('pathname')).toHaveTextContent('/admin/manage');
    });
    expect(screen.getByTestId('search')).toHaveTextContent('tab=users');
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
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValueOnce(false);
    renderProvider();

    fireEvent.click(screen.getByRole('button', { name: 'Open palette' }));
    const input = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    fireEvent.change(input, { target: { value: 'sign out' } });
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(confirmSpy).toHaveBeenCalledWith('Sign out of PrintFarmer?');
    expect(authState.logout).not.toHaveBeenCalled();
    confirmSpy.mockRestore();
  });

  it('runs the destructive action when the user confirms', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValueOnce(true);
    renderProvider();

    fireEvent.click(screen.getByRole('button', { name: 'Open palette' }));
    const input = await screen.findByRole('combobox', { name: 'Search settings command palette' });
    fireEvent.change(input, { target: { value: 'sign out' } });
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(authState.logout).toHaveBeenCalled();
    });
    confirmSpy.mockRestore();
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
