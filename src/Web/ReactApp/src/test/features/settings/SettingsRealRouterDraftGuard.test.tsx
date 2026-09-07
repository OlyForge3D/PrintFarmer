/**
 * Real-router dirty-draft protection (#2525).
 *
 * These cases deliberately render the actual `App`, so the router composition
 * under test is the one the application ships (`AppRouterProvider`). That is the
 * whole point of the regression: `SettingsShell` mounts its `useBlocker` adapter
 * only when a data-router context exists and otherwise reports "unblocked"
 * unconditionally, so a `MemoryRouter`/`createMemoryRouter` stand-in would either
 * hide the defect or satisfy the data-router branch for free.
 *
 * The navbar itself is stubbed down to plain react-router `NavLink`s. The defect
 * is in the router composition, not in the navbar's entry list, and #2526 is
 * concurrently rewriting that list in `Layout.tsx` — coupling this regression to
 * it would make the suite fail for unrelated reasons. What matters here is that
 * the links live in app chrome *outside* `SettingsShell` and navigate through the
 * real router, exactly as the real navbar's `NavLink`s do.
 */
import '@testing-library/jest-dom';
import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const authState = vi.hoisted(() => ({
  roles: ['farm_admin'] as string[],
}));

const settingsApi = vi.hoisted(() => ({
  fetchSettingsMetadata: vi.fn(),
  fetchSettingsGroups: vi.fn(),
  fetchSettingsUnified: vi.fn(),
  saveSettingsValues: vi.fn(),
}));

const settingsState = vi.hoisted(() => ({
  values: {} as Record<string, Record<string, unknown>>,
}));

// ---------------------------------------------------------------------------
// App shell plumbing (mirrors src/test/App.admin-routing.test.tsx)
// ---------------------------------------------------------------------------

vi.mock('@/common/hooks/useUnifiedLogging', () => ({
  useUnifiedLogging: () => ({ logger: { info: vi.fn(), warn: vi.fn(), error: vi.fn() } }),
}));

vi.mock('@/services/api/setupApi', () => ({
  getSetupStatus: vi.fn().mockResolvedValue({ needsSetup: false }),
}));

vi.mock('@/services/assetService', () => ({
  assetService: { initialize: vi.fn(() => Promise.resolve()) },
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    connect: vi.fn().mockResolvedValue(undefined),
    disconnect: vi.fn().mockResolvedValue(undefined),
    onFailureDetected: vi.fn(() => vi.fn()),
    onQueueEvent: vi.fn(() => vi.fn()),
    onConnectionStateChange: vi.fn(() => vi.fn()),
    replaceQueueResourceSubscriptions: vi.fn().mockResolvedValue(undefined),
  },
}));

vi.mock('@/services/harvest-signalr', () => ({
  signalRService: { connect: vi.fn().mockResolvedValue(undefined) },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: () => 'http://localhost:5245',
  getAuthHeaders: () => ({}),
  getHubUrl: (hubPath: string) => `http://localhost:5245${hubPath}`,
}));

vi.mock('@/contexts/ThemeContext', () => ({
  ThemeProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/common/contexts/AuthContext', () => ({
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/contexts/SlicerUIContext', () => ({
  SlicerUIProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/contexts/SlicerContext', () => ({
  SlicerProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/common/components/ErrorBoundary', () => ({
  ErrorBoundary: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  RouteErrorBoundary: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/features/auth/components/ProtectedRoute', () => ({
  ProtectedRoute: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/features/auth/components/SetupWizard', () => ({
  SetupWizard: () => <div>SetupWizardMock</div>,
}));

vi.mock('@/common/hooks/useSystemCapabilities', () => ({
  useSystemCapabilities: () => ({
    data: { slicingEnabled: true, modelFilesEnabled: true, architecture: 'x64', platformNote: '' },
  }),
}));

vi.mock('sonner', () => ({
  Toaster: () => null,
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
}));

vi.mock('@tanstack/react-query-devtools', () => ({
  ReactQueryDevtools: () => null,
}));

/**
 * Minimal app chrome. Real `NavLink`s outside the settings shell, plus the
 * command-palette provider the real `Layout` mounts (SettingsShell consumes it).
 */
vi.mock('@/common/components/Layout', async () => {
  const { NavLink, Outlet } = await import('react-router');
  const { GlobalCommandPaletteProvider } = await import(
    '@/features/settings/components/GlobalCommandPaletteProvider'
  );
  return {
    Layout: () => (
      <GlobalCommandPaletteProvider>
        <nav aria-label="Main">
          <NavLink to="/printers">Printers</NavLink>
          <NavLink to="/projects">Projects</NavLink>
          <NavLink to="/admin/settings?scope=system&tab=general&sub=system">
            Farm &amp; Admin Settings
          </NavLink>
        </nav>
        <Outlet />
      </GlobalCommandPaletteProvider>
    ),
  };
});

// ---------------------------------------------------------------------------
// Navigation targets — cheap stand-ins so assertions stay about the URL
// ---------------------------------------------------------------------------

vi.mock('@/features/printers/pages/PrintersPage', () => ({
  PrintersPage: () => <div>PrintersPageMock</div>,
}));

vi.mock('@/features/projects/pages/ProjectsPage', () => ({
  ProjectsPage: () => <div>ProjectsPageMock</div>,
}));

vi.mock('@/features/printers/components/PrinterDashboard', () => ({
  PrinterDashboard: () => <div>PrinterDashboardMock</div>,
}));

// ---------------------------------------------------------------------------
// Settings shell dependencies (mirrors AdminWorkspaceIntegratedFlow.test.tsx)
// ---------------------------------------------------------------------------

vi.mock('@/features/auth/hooks/useAuth', () => {
  const identity = () => ({
    user: { id: 'user-1', email: 'admin@test.com', isActive: true, roles: authState.roles },
    isAuthenticated: true,
    isLoading: false,
    hasRole: (role: string) => authState.roles.includes(role),
    hasPermission: () => authState.roles.includes('farm_admin'),
    logout: vi.fn(),
  });
  return { useAuth: identity, useAuthInternal: identity };
});

vi.mock('@/services/settingsApi', () => settingsApi);

vi.mock('@/common/hooks/useTheme', () => ({
  useTheme: () => ({
    theme: 'dark',
    setTheme: vi.fn(),
    themes: ['light', 'dark'],
    isLight: false,
    isDark: true,
  }),
}));

vi.mock('@/common/components/ThemeSwitcher', () => ({
  ThemeSwitcher: () => <div data-testid="theme-switcher">Theme Switcher</div>,
}));

vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: () => ({ startTour: vi.fn(), hasSeenTour: true, resetTour: vi.fn() }),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true, workerCount: 1 }),
}));

vi.mock('@/features/admin/settings/section-renderers', () => ({
  getSectionRenderer: () => undefined,
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

vi.mock('@/features/printer-groups/pages/PrinterGroupsPage', () => ({
  PrinterGroupsPage: () => <div>Printer Groups Page</div>,
}));

vi.mock('@/features/nfc/pages/NfcDevicesPage', () => ({
  NfcDevicesPage: () => <div>NFC Devices Page</div>,
}));

vi.mock('@/features/nfc/pages/NfcBindingsPage', () => ({
  NfcBindingsPage: () => <div>NFC Bindings Page</div>,
}));

vi.mock('@/features/cameras/pages/CamerasPage', () => ({
  CamerasPage: () => <div>Cameras Page</div>,
}));

vi.mock('@/features/admin/pages/CustomFieldsAdminPage', () => ({
  CustomFieldsAdminPage: () => <div>Custom Fields Page</div>,
}));

vi.mock('@/features/admin/pages/TagAdminPage', () => ({
  TagAdminPage: () => <div>Tags Page</div>,
}));

vi.mock('@/features/admin/pages/UserManagementPage', () => ({
  UserManagementPage: () => <div>Users Page</div>,
}));

vi.mock('@/features/admin/pages/RoleManagementPage', () => ({
  RoleManagementPage: () => <div>Roles Page</div>,
}));

vi.mock('@/features/profile/pages/ApiKeysPage', () => ({
  ApiKeysPage: () => <div>API Keys</div>,
}));

vi.mock('@/features/profile/pages/PasskeysPage', () => ({
  PasskeysPage: () => <div>Passkeys</div>,
}));

vi.mock('@/features/notifications/pages/NotificationPreferencesPage', () => ({
  NotificationPreferencesPage: () => <div>Notifications</div>,
}));

import App from '../../../App';
import { queryClient } from '@/services/queryClient';

const SETTINGS_URL = '/admin/settings?scope=system&tab=general&sub=system';

function numberProp(name: string, label: string) {
  return {
    name,
    type: 'number',
    attributes: [],
    display: { name: label, inputType: 'Number', minValue: 1, maxValue: 100000 },
  };
}

function booleanProp(name: string, label: string) {
  return { name, type: 'boolean', attributes: [], display: { name: label, inputType: 'Boolean' } };
}

function installSettingsApiDefaults() {
  settingsApi.fetchSettingsMetadata.mockResolvedValue([
    {
      key: 'SystemLog',
      className: 'SystemLogSettings',
      displayName: 'System Logging',
      description: 'Database logging configuration.',
      group: 'System',
      order: 1,
      properties: [booleanProp('enabled', 'Enable Database Logging'), numberProp('retentionDays', 'Retention Days')],
    },
  ]);
  settingsApi.fetchSettingsGroups.mockResolvedValue([{ key: 'System', displayName: 'System', order: 1 }]);
  settingsState.values = { SystemLog: { enabled: true, retentionDays: 30 } };
  settingsApi.fetchSettingsUnified.mockImplementation(() =>
    Promise.resolve({ SystemLog: { ...settingsState.values.SystemLog } }),
  );
  settingsApi.saveSettingsValues.mockImplementation((key: string, values: Record<string, unknown>) => {
    settingsState.values[key] = { ...values };
    return Promise.resolve();
  });
}

function currentUrl() {
  return `${window.location.pathname}${window.location.search}`;
}

/** Drive a real browser history traversal and let react-router settle. */
async function traverseHistory(direction: 'back' | 'forward') {
  await act(async () => {
    if (direction === 'back') {
      window.history.back();
    } else {
      window.history.forward();
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  });
}

async function makeDraftDirty(value = '45') {
  const input = await screen.findByLabelText('Retention Days');
  fireEvent.change(input, { target: { value } });
  await waitFor(() => expect(screen.getByLabelText('Retention Days')).toHaveValue(Number(value)));
  // The input echoing the new value is not enough: the blocker reads the shell's
  // `dirtyByGroup` registry, which the group card publishes upward a tick later.
  // The save bar is rendered from that same state, so waiting for it guarantees
  // the guard is actually armed before the test navigates.
  await waitFor(() => expect(screen.getByTestId('admin-save-bar')).toBeInTheDocument());
  return input;
}

function expectDraftModal() {
  return screen.findByText('You have unsaved changes. Do you want to stay on this page or discard your changes?');
}

async function openSettingsFromNavbar() {
  fireEvent.click(screen.getByRole('link', { name: 'Farm & Admin Settings' }));
  await screen.findByLabelText('Retention Days');
}

describe('dirty settings drafts survive real-router navigation (#2525)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    authState.roles = ['farm_admin'];
    installSettingsApiDefaults();
    queryClient.clear();
  });

  it('blocks a main-navbar exit and keeps the draft when the user stays', async () => {
    window.history.pushState({}, '', SETTINGS_URL);
    render(<App />);
    await makeDraftDirty('45');

    fireEvent.click(screen.getByRole('link', { name: 'Printers' }));

    expect(await expectDraftModal()).toBeInTheDocument();
    await waitFor(() => expect(currentUrl()).toBe(SETTINGS_URL));
    expect(screen.queryByText('PrintersPageMock')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Stay' }));

    await waitFor(() => {
      expect(
        screen.queryByText('You have unsaved changes. Do you want to stay on this page or discard your changes?'),
      ).not.toBeInTheDocument();
    });
    await waitFor(() => expect(currentUrl()).toBe(SETTINGS_URL));
    expect(screen.getByLabelText('Retention Days')).toHaveValue(45);
  });

  it('lets a main-navbar exit through once the user discards', async () => {
    window.history.pushState({}, '', SETTINGS_URL);
    render(<App />);
    await makeDraftDirty('45');

    fireEvent.click(screen.getByRole('link', { name: 'Printers' }));
    await expectDraftModal();

    fireEvent.click(screen.getByRole('button', { name: 'Discard Changes' }));

    expect(await screen.findByText('PrintersPageMock')).toBeInTheDocument();
    await waitFor(() => expect(currentUrl()).toBe('/printers'));
  });

  it('does not prompt when a main-navbar exit leaves no unsaved edits', async () => {
    window.history.pushState({}, '', SETTINGS_URL);
    render(<App />);
    await screen.findByLabelText('Retention Days');

    fireEvent.click(screen.getByRole('link', { name: 'Printers' }));

    expect(await screen.findByText('PrintersPageMock')).toBeInTheDocument();
    expect(
      screen.queryByText('You have unsaved changes. Do you want to stay on this page or discard your changes?'),
    ).not.toBeInTheDocument();
  });

  it('blocks browser Back and keeps the draft when the user stays', async () => {
    window.history.pushState({}, '', '/printers');
    render(<App />);
    await screen.findByText('PrintersPageMock');
    await openSettingsFromNavbar();
    await makeDraftDirty('45');

    await traverseHistory('back');

    expect(await expectDraftModal()).toBeInTheDocument();
    await waitFor(() => expect(currentUrl()).toBe(SETTINGS_URL));
    expect(screen.queryByText('PrintersPageMock')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Stay' }));

    await waitFor(() => {
      expect(
        screen.queryByText('You have unsaved changes. Do you want to stay on this page or discard your changes?'),
      ).not.toBeInTheDocument();
    });
    await waitFor(() => expect(currentUrl()).toBe(SETTINGS_URL));
    expect(screen.getByLabelText('Retention Days')).toHaveValue(45);
  });

  it('lets browser Back through once the user discards', async () => {
    window.history.pushState({}, '', '/printers');
    render(<App />);
    await screen.findByText('PrintersPageMock');
    await openSettingsFromNavbar();
    await makeDraftDirty('45');

    await traverseHistory('back');
    await expectDraftModal();

    fireEvent.click(screen.getByRole('button', { name: 'Discard Changes' }));

    expect(await screen.findByText('PrintersPageMock')).toBeInTheDocument();
    await waitFor(() => expect(currentUrl()).toBe('/printers'));
  });

  it('blocks browser Forward and keeps the draft when the user stays', async () => {
    window.history.pushState({}, '', '/printers');
    render(<App />);
    await screen.findByText('PrintersPageMock');
    await openSettingsFromNavbar();

    fireEvent.click(screen.getByRole('link', { name: 'Projects' }));
    await screen.findByText('ProjectsPageMock');

    await traverseHistory('back');
    await screen.findByLabelText('Retention Days');
    await makeDraftDirty('45');

    await traverseHistory('forward');

    expect(await expectDraftModal()).toBeInTheDocument();
    await waitFor(() => expect(currentUrl()).toBe(SETTINGS_URL));
    expect(screen.queryByText('ProjectsPageMock')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Stay' }));

    await waitFor(() => {
      expect(
        screen.queryByText('You have unsaved changes. Do you want to stay on this page or discard your changes?'),
      ).not.toBeInTheDocument();
    });
    await waitFor(() => expect(currentUrl()).toBe(SETTINGS_URL));
    expect(screen.getByLabelText('Retention Days')).toHaveValue(45);
  });
});
