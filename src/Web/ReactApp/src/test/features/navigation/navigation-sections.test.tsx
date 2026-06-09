import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Layout } from '@/common/components/Layout';

const createTestQueryClient = () => new QueryClient({
  defaultOptions: {
    queries: { retry: false },
    mutations: { retry: false },
  },
});

let mockUserRole = 'farm_admin';

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    user: { id: '1', email: 'admin@test.com', role: mockUserRole, isActive: true, username: 'admin' },
    logout: vi.fn(),
    isAuthenticated: true,
    hasRole: (role: string) => role === mockUserRole,
    hasPermission: () => true,
  }),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({
    isSlicerAvailable: true,
    isLoading: false,
  }),
}));

vi.mock('@/contexts/ThemeContext', () => ({
  useTheme: () => ({
    theme: 'light',
    setTheme: vi.fn(),
  }),
}));

vi.mock('@/common/hooks/useSignalR', () => ({
  useSignalRConnection: () => ({
    isConnected: true,
  }),
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    connect: vi.fn().mockResolvedValue(undefined),
    onPrinterStatusUpdate: vi.fn().mockReturnValue(() => {}),
    onAutoDispatchStateChanged: vi.fn().mockReturnValue(() => {}),
  },
}));

vi.mock('@/features/tasks', () => ({
  TasksBadge: () => null,
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAllAutoDispatchStatuses: () => ({
    data: [],
    isLoading: false,
  }),
}));

describe('Navigation rail sections', () => {
  const renderLayout = () => {
    const queryClient = createTestQueryClient();
    return render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <Layout />
        </MemoryRouter>
      </QueryClientProvider>,
    );
  };

  beforeEach(() => {
    mockUserRole = 'farm_admin';
    localStorage.clear();
  });

  it('renders the new left rail sections with grouped child links', () => {
    const { container } = renderLayout();

    expect(screen.getAllByText('Dashboard').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Printers').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Files').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Slicer').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Settings').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Admin').length).toBeGreaterThan(0);

    expect(screen.getByRole('link', { name: /overview/i })).toHaveAttribute('href', '/dashboard');
    expect(screen.getByRole('link', { name: /print queue/i })).toHaveAttribute('href', '/printQueue');
    expect(container.querySelector('a[href="/files"]')).not.toBeNull();
    expect(container.querySelector('a[href="/projects"]')).not.toBeNull();
    expect(screen.getByRole('link', { name: /preferences/i })).toHaveAttribute('href', '/settings');
    expect(screen.getByRole('link', { name: /api keys/i })).toHaveAttribute('href', '/profile/api-keys');
    expect(container.querySelector('a[href="/admin/settings"]')).not.toBeNull();
    expect(container.querySelector('a[href="/admin/manage"]')).not.toBeNull();
  });

  it('hides the admin section for authenticated non-admin users while keeping personal settings', () => {
    mockUserRole = 'operator';
    const { container } = renderLayout();

    expect(screen.getByRole('link', { name: /preferences/i })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /api keys/i })).toBeInTheDocument();
    expect(container.querySelector('a[href="/admin/settings"]')).toBeNull();
    expect(container.querySelector('a[href="/admin/manage"]')).toBeNull();
    expect(screen.queryByText('Admin')).not.toBeInTheDocument();
  });

  it('keeps the skip link and main navigation landmark available', () => {
    renderLayout();

    expect(screen.getByRole('link', { name: /skip to main content/i })).toHaveAttribute('href', '#main-content');
    expect(screen.getByRole('navigation', { name: 'Main navigation' })).toBeInTheDocument();
  });

  it('makes the mobile drawer inert when closed and moves focus into it when opened', async () => {
    const { container } = renderLayout();

    const drawerWrapper = container.querySelector('#mobile-navigation-drawer')?.parentElement;
    expect(drawerWrapper).toHaveAttribute('inert');

    const menuButton = screen.getByRole('button', { name: 'Open navigation menu' });
    fireEvent.click(menuButton);

    expect(drawerWrapper).not.toHaveAttribute('inert');
    const dialog = await screen.findByRole('dialog', { name: 'Mobile navigation drawer' });
    expect(screen.getByText('Navigation menu opened.')).toBeInTheDocument();
    await waitFor(() => {
      expect(within(dialog).getByRole('button', { name: 'Close navigation menu' })).toHaveFocus();
    });

    fireEvent.keyDown(window, { key: 'Escape' });
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Mobile navigation drawer' })).not.toBeInTheDocument();
      expect(screen.getByText('Navigation menu closed.')).toBeInTheDocument();
    });
  });

  it('opens collapsed rail popovers on click, focuses the first link, and restores focus on Escape', async () => {
    localStorage.setItem('pf_navbar_collapsed', 'true');
    renderLayout();

    const filesButton = screen.getByRole('button', { name: 'Files' });
    fireEvent.click(filesButton);

    const dialog = screen.getByRole('dialog', { name: 'Files navigation' });
    const filesLink = within(dialog).getByRole('link', { name: /^files$/i });
    expect(dialog).toBeInTheDocument();
    expect(filesLink).toBeInTheDocument();
    expect(within(dialog).getByRole('link', { name: /projects/i })).toBeInTheDocument();

    await waitFor(() => {
      expect(filesLink).toHaveFocus();
    });

    fireEvent.keyDown(window, { key: 'Escape' });
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Files navigation' })).not.toBeInTheDocument();
      expect(filesButton).toHaveFocus();
    });
  });
});
