import { describe, it, expect, vi } from 'vitest';
import { render, fireEvent, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SettingsShell } from '@/features/settings/pages/SettingsShell';

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

function renderSettings(initialRoute = '/settings') {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialRoute]}>
        <SettingsShell />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('SettingsShell', () => {
  it('renders the settings heading and tabs', () => {
    renderSettings();
    expect(screen.getByRole('heading', { level: 1, name: 'Settings' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'General' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Filament' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Users' })).toBeInTheDocument();
  });

  it('defaults to the General tab', () => {
    renderSettings();
    const generalTab = screen.getByRole('tab', { name: 'General' });
    expect(generalTab).toHaveAttribute('aria-selected', 'true');
  });

  it('switches tab on click', () => {
    renderSettings();
    const filamentTab = screen.getByRole('tab', { name: 'Filament' });
    fireEvent.click(filamentTab);
    expect(filamentTab).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'General' })).toHaveAttribute('aria-selected', 'false');
  });

  it('deep-links to a specific tab via URL', () => {
    renderSettings('/settings?tab=notifications');
    const notificationsTab = screen.getByRole('tab', { name: 'Notifications' });
    expect(notificationsTab).toHaveAttribute('aria-selected', 'true');
  });

  it('renders search input', () => {
    renderSettings();
    expect(screen.getByRole('searchbox')).toBeInTheDocument();
  });

  it('filters tabs by search query', () => {
    renderSettings('/settings?q=slicer');
    // Should show Slicing tab (keyword match)
    expect(screen.getByRole('tab', { name: 'Slicing' })).toBeInTheDocument();
    // Tabs that don't match should be hidden
    expect(screen.queryByRole('tab', { name: 'Users' })).not.toBeInTheDocument();
  });

  it('shows empty state when no tabs match search', () => {
    renderSettings('/settings?q=xyznonexistent');
    expect(screen.getByText(/No settings found matching/)).toBeInTheDocument();
  });

  it('deep-links with both tab and query params', () => {
    renderSettings('/settings?tab=hardware&q=printer');
    const hardwareTab = screen.getByRole('tab', { name: 'Hardware' });
    expect(hardwareTab).toHaveAttribute('aria-selected', 'true');
  });

  it('updates search value from input', () => {
    renderSettings();
    const searchInput = screen.getByLabelText('Search settings');
    fireEvent.change(searchInput, { target: { value: 'email' } });
    // After typing, the notifications tab should match (keyword 'email')
    expect(screen.getByRole('tab', { name: 'Notifications' })).toBeInTheDocument();
  });
});
