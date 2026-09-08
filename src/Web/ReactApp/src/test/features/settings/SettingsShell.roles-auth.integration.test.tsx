import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthProvider } from '@/common/contexts/AuthContext';
import { GlobalCommandPaletteProvider } from '@/features/settings/components/GlobalCommandPaletteProvider';
import { SettingsShell } from '@/features/settings/pages/SettingsShell';
import type { UserDto } from '@/types/api';

vi.mock('@/services/api/authApi', () => ({
  getCurrentUser: vi.fn(),
  login: vi.fn(),
  register: vi.fn(),
  logout: vi.fn(),
}));

vi.mock('@/common/components/ThemeSwitcher', () => ({
  ThemeSwitcher: () => <div data-testid="theme-switcher">Theme Switcher</div>,
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

vi.mock('@/features/admin/pages/SettingsPage', () => ({
  SettingsPage: () => <div data-testid="legacy-settings-page">Legacy Settings Page</div>,
}));

vi.mock('@/features/admin/pages/RoleManagementPage', () => ({
  RoleManagementPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="roles-editor" data-embedded={String(embedded)}>
      Roles editor
    </div>
  ),
}));

vi.mock('@/features/admin/pages/UserManagementPage', () => ({
  UserManagementPage: () => <div data-testid="accounts-editor">Accounts editor</div>,
}));

vi.mock('@/features/admin/pages/TagAdminPage', () => ({
  TagAdminPage: () => <div data-testid="tags-editor">Tags editor</div>,
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

vi.mock('@/features/settings/components/FarmSettingsSection', () => ({
  FarmSettingsSection: () => <div data-testid="farm-settings-section">Farm Settings Section</div>,
}));

vi.mock('@/features/settings/components/IntegrationSettingsCards', () => ({
  SpoolmanSettingsCard: () => <div data-testid="spoolman-settings">Spoolman settings</div>,
  HomeAssistantSettingsCard: () => <div data-testid="home-assistant-settings">Home Assistant settings</div>,
}));

vi.mock('@/features/settings/components/TelegramSettingsCard', () => ({
  TelegramSettingsCard: () => <div data-testid="telegram-settings">Telegram settings</div>,
}));

vi.mock('sonner', () => ({
  toast: {
    info: vi.fn(),
    error: vi.fn(),
    warning: vi.fn(),
    success: vi.fn(),
  },
}));

import { getCurrentUser } from '@/services/api/authApi';

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: false } },
});

function LocationProbe() {
  const location = useLocation();
  return (
    <>
      <div data-testid="location-pathname">{location.pathname}</div>
      <div data-testid="location-search">{location.search}</div>
    </>
  );
}

function renderSettings(initialRoute = '/admin/settings?scope=system&tab=users&sub=roles') {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialRoute]}>
        <AuthProvider>
          <GlobalCommandPaletteProvider>
            <SettingsShell routeScope="system" />
          </GlobalCommandPaletteProvider>
          <LocationProbe />
        </AuthProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('SettingsShell roles delegate auth integration (#2557)', () => {
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
    vi.clearAllMocks();
    localStorage.clear();
    queryClient.clear();
  });

  it('lets a /auth/me user with only roles:admin reach the Roles page deep link', async () => {
    const user: UserDto = {
      id: 'user-1',
      username: 'roles-delegate',
      email: 'delegate@example.com',
      roles: ['farm_user'],
      permissions: ['roles:admin'],
      isActive: true,
    } as UserDto;
    vi.mocked(getCurrentUser).mockResolvedValue(user);
    localStorage.setItem('auth-token', 'test-token');

    renderSettings();

    await waitFor(() => expect(screen.getByTestId('roles-editor')).toBeInTheDocument());
    expect(screen.getByTestId('roles-editor')).toHaveAttribute('data-embedded', 'true');
    expect(screen.getByTestId('location-pathname')).toHaveTextContent('/admin/settings');
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=system');
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=users');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=roles');
    expect(screen.queryByText(/don't have permission to view/i)).not.toBeInTheDocument();
  });
});
