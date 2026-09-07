import '@testing-library/jest-dom';
import React from 'react';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { AdminControlCenterPage } from '@/features/admin/pages/AdminControlCenterPage';
import { SettingsShell } from '@/features/settings/pages/SettingsShell';
import { GlobalCommandPaletteProvider } from '@/features/settings/components/GlobalCommandPaletteProvider';
import type { AdminOverviewDto } from '@/types/adminOverview';

const authState = vi.hoisted(() => ({
  roles: ['farm_admin'] as string[],
  grants: new Set<string>(),
}));

const overviewState = vi.hoisted(() => ({
  result: {
    data: undefined as AdminOverviewDto | undefined,
    isLoading: false,
    isError: false,
    error: null as unknown,
    isFetching: false,
    refetch: vi.fn(),
  },
}));

const settingsApi = vi.hoisted(() => ({
  fetchSettingsMetadata: vi.fn(),
  fetchSettingsGroups: vi.fn(),
  fetchSettingsUnified: vi.fn(),
  saveSettingsValues: vi.fn(),
}));

const toastMocks = vi.hoisted(() => ({
  success: vi.fn(),
  error: vi.fn(),
  info: vi.fn(),
  warning: vi.fn(),
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    user: {
      id: 'user-1',
      email: authState.roles.includes('farm_admin') ? 'admin@test.com' : 'delegate@test.com',
      isActive: true,
      roles: authState.roles,
    },
    isAuthenticated: true,
    isLoading: false,
    hasRole: (role: string) => authState.roles.includes(role),
    hasPermission: (resource: string, action: string) =>
      authState.roles.includes('farm_admin') || authState.grants.has(`${resource}:${action}`),
    logout: vi.fn(),
  }),
  useAuthInternal: () => ({
    user: {
      id: 'user-1',
      email: authState.roles.includes('farm_admin') ? 'admin@test.com' : 'delegate@test.com',
      isActive: true,
      roles: authState.roles,
    },
    isAuthenticated: true,
    isLoading: false,
    hasRole: (role: string) => authState.roles.includes(role),
    hasPermission: (resource: string, action: string) =>
      authState.roles.includes('farm_admin') || authState.grants.has(`${resource}:${action}`),
    logout: vi.fn(),
  }),
}));

vi.mock('@/features/admin/hooks/useAdminOverview', () => ({
  useAdminOverview: () => overviewState.result,
}));

vi.mock('@/services/settingsApi', () => settingsApi);

vi.mock('sonner', () => ({ toast: toastMocks }));

vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: () => ({ startTour: vi.fn(), hasSeenTour: true, resetTour: vi.fn() }),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true, workerCount: 1 }),
}));

vi.mock('@/features/admin/settings/section-renderers', () => ({
  getSectionRenderer: () => undefined,
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

vi.mock('@/common/components/ThemeSwitcher', () => ({
  ThemeSwitcher: () => <div data-testid="theme-switcher">Theme Switcher</div>,
}));

vi.mock('@/features/printer-groups/pages/PrinterGroupsPage', () => ({
  PrinterGroupsPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="printer-groups-page" data-embedded={String(embedded)}>Printer Groups Page</div>
  ),
}));

vi.mock('@/features/nfc/pages/NfcDevicesPage', () => ({
  NfcDevicesPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="nfc-devices-page" data-embedded={String(embedded)}>NFC Devices Page</div>
  ),
}));

vi.mock('@/features/nfc/pages/NfcBindingsPage', () => ({
  NfcBindingsPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="nfc-bindings-page" data-embedded={String(embedded)}>NFC Bindings Page</div>
  ),
}));

vi.mock('@/features/cameras/pages/CamerasPage', () => ({
  CamerasPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="cameras-page" data-embedded={String(embedded)}>Cameras Page</div>
  ),
}));

vi.mock('@/features/admin/pages/CustomFieldsAdminPage', () => ({
  CustomFieldsAdminPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="custom-fields-page" data-embedded={String(embedded)}>Custom Fields Page</div>
  ),
}));

vi.mock('@/features/webhooks/pages/WebhooksAdminPage', () => ({
  WebhooksAdminPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="webhooks-page" data-embedded={String(embedded)}>Webhooks Page</div>
  ),
}));

vi.mock('@/features/admin/pages/TagAdminPage', () => ({
  TagAdminPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="tags-page" data-embedded={String(embedded)}>Tags Page</div>
  ),
}));

vi.mock('@/features/admin/pages/UserManagementPage', () => ({
  UserManagementPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="users-page" data-embedded={String(embedded)}>Users Page</div>
  ),
}));

vi.mock('@/features/admin/pages/RoleManagementPage', () => ({
  RoleManagementPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="roles-page" data-embedded={String(embedded)}>Roles Page</div>
  ),
}));

vi.mock('@/features/quotas/pages/QuotaManagementPage', () => ({
  QuotaManagementPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="quotas-page" data-embedded={String(embedded)}>Print Quotas</div>
  ),
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

vi.mock('@/features/profile/pages/ApiKeysPage', () => ({
  ApiKeysPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="api-keys-page" data-embedded={String(embedded)}>API Keys</div>
  ),
}));

vi.mock('@/features/profile/pages/PasskeysPage', () => ({
  PasskeysPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="passkeys-page" data-embedded={String(embedded)}>Passkeys</div>
  ),
}));

vi.mock('@/features/notifications/pages/NotificationPreferencesPage', () => ({
  NotificationPreferencesPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="notifications-page" data-embedded={String(embedded)}>Notifications</div>
  ),
}));

vi.mock('@/features/slicer/pages/SlicerProfilesPage', () => ({
  SlicerProfilesPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="slicer-profiles-page" data-embedded={String(embedded)}>Slicer Profiles</div>
  ),
}));

function numberProp(name: string, label: string) {
  return {
    name,
    type: 'number',
    attributes: [],
    display: { name: label, inputType: 'Number', minValue: 1, maxValue: 100000 },
  };
}

function booleanProp(name: string, label: string) {
  return {
    name,
    type: 'boolean',
    attributes: [],
    display: { name: label, inputType: 'Boolean' },
  };
}

function makeOverview(overrides: Partial<AdminOverviewDto> = {}): AdminOverviewDto {
  return {
    checkedAt: '2026-09-06T22:00:00Z',
    overallStatus: 'Degraded',
    subsystems: [
      { key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' },
      { key: 'workers', name: 'Slicer Workers', status: 'Degraded', detail: '1 worker has queued jobs' },
    ],
    attention: [
      {
        key: 'system-log-retention',
        severity: 'Warning',
        title: 'System log retention needs review',
        detail: 'Retention is lower than the recommended operating window.',
        actionLabel: 'Open System Config',
        actionDestinationId: 'gen-system',
      },
    ],
    ...overrides,
  };
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
      properties: [
        booleanProp('enabled', 'Enable Database Logging'),
        numberProp('retentionDays', 'Retention Days'),
      ],
    },
    {
      key: 'NetworkDiscovery',
      className: 'NetworkDiscoverySettings',
      displayName: 'Network Discovery',
      description: 'Discovery cadence configuration.',
      group: 'Networking',
      order: 2,
      properties: [
        booleanProp('enableDiscovery', 'Enable Discovery'),
        numberProp('scanIntervalMinutes', 'Scan Interval Minutes'),
      ],
    },
  ]);
  settingsApi.fetchSettingsGroups.mockResolvedValue([
    { key: 'System', displayName: 'System', order: 1 },
    { key: 'Networking', displayName: 'Networking', order: 2 },
  ]);
  settingsApi.fetchSettingsUnified.mockResolvedValue({
    SystemLog: { enabled: true, retentionDays: 30 },
    NetworkDiscovery: { enableDiscovery: true, scanIntervalMinutes: 10 },
  });
  settingsApi.saveSettingsValues.mockResolvedValue(undefined);
}

function LocationProbe() {
  const location = useLocation();
  return (
    <>
      <div data-testid="location-pathname">{location.pathname}</div>
      <div data-testid="location-search">{location.search}</div>
    </>
  );
}

function WorkerRouteProbe() {
  const location = useLocation();
  const workerTab = new URLSearchParams(location.search).get('workerTab') ?? 'workers';
  return (
    <main>
      <h1>Workers & Jobs</h1>
      <button type="button" aria-pressed={workerTab === 'workers'}>Workers</button>
      <button type="button" aria-pressed={workerTab === 'jobs'}>Jobs</button>
      <p data-testid="worker-tab">{workerTab}</p>
    </main>
  );
}

function SimpleRoute({ title }: { title: string }) {
  return (
    <main>
      <h1>{title}</h1>
    </main>
  );
}

function renderWorkspace(initialRoute = '/admin') {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialRoute]}>
        <GlobalCommandPaletteProvider>
          <Routes>
            <Route path="/admin" element={<AdminControlCenterPage />} />
            <Route path="/admin/settings" element={<SettingsShell routeScope="system" />} />
            <Route path="/settings" element={<SettingsShell routeScope="user" />} />
            <Route path="/admin/workers" element={<WorkerRouteProbe />} />
            <Route path="/admin/power-monitors" element={<SimpleRoute title="Power Monitors" />} />
            <Route path="/admin/status" element={<SimpleRoute title="System Status" />} />
            <Route path="/admin/login-audit" element={<SimpleRoute title="Login Audit" />} />
            <Route path="/admin/data-management" element={<SimpleRoute title="Data Management" />} />
            <Route path="/locations" element={<SimpleRoute title="Locations" />} />
            <Route path="/catalog" element={<SimpleRoute title="Catalog" />} />
          </Routes>
          <LocationProbe />
        </GlobalCommandPaletteProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function grantOnly(...grants: string[]) {
  authState.roles = ['farm_user'];
  authState.grants = new Set(grants);
}

async function openSystemSettings() {
  renderWorkspace('/admin/settings?scope=system&tab=general&sub=system');
  await screen.findByLabelText('Retention Days');
}

describe('Admin workspace integrated flow (#2507)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    authState.roles = ['farm_admin'];
    authState.grants = new Set();
    overviewState.result = {
      data: makeOverview(),
      isLoading: false,
      isError: false,
      error: null,
      isFetching: false,
      refetch: vi.fn(),
    };
    installSettingsApiDefaults();
    window.localStorage.setItem('pf.settings.mode', 'everything');
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: query.includes('prefers-reduced-motion'),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    });
    Element.prototype.scrollIntoView = vi.fn();
  });

  it('navigates from dashboard attention to system settings, then exact workspace field search retains history and one page heading', async () => {
    renderWorkspace('/admin');

    expect(screen.getAllByRole('heading', { level: 1, name: 'Admin Control Center' })).toHaveLength(1);
    expect(screen.getByTestId('admin-hub-attention')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('link', { name: 'Open System Config' }));

    await waitFor(() => expect(screen.getByTestId('location-pathname')).toHaveTextContent('/admin/settings'));
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=general');
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=system');
    expect(screen.getAllByRole('heading', { level: 1, name: 'Farm & Admin Settings' })).toHaveLength(1);
    await screen.findByLabelText('Retention Days');

    const workspaceSearch = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.focus(workspaceSearch);
    fireEvent.change(workspaceSearch, { target: { value: 'retention days' } });
    fireEvent.click(await screen.findByRole('option', { name: /Retention Days/i }));

    await waitFor(() => expect(screen.getByTestId('location-search')).toHaveTextContent('field=SystemLog.retentionDays'));
    expect(screen.getByTestId('location-search')).toHaveTextContent('q=retention+days');
    expect(screen.queryByRole('heading', { level: 1, name: 'User Settings' })).not.toBeInTheDocument();
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
  });

  it('keeps delegated destinations, standalone links, quotas, workers jobs, and role-only slicer profiles consistent', async () => {
    grantOnly('dispatch-settings:manage');
    const workersView = renderWorkspace('/admin');
    const workersLink = screen.getByRole('link', { name: /Workers & Jobs/i });
    expect(workersLink).toHaveAttribute('href', '/admin/workers?workerTab=jobs');
    fireEvent.click(workersLink);
    await waitFor(() => expect(screen.getByTestId('worker-tab')).toHaveTextContent('jobs'));
    workersView.unmount();

    grantOnly('power_monitors:admin');
    const powerView = renderWorkspace('/admin');
    expect(screen.getByRole('link', { name: /Power Monitors/i })).toHaveAttribute('href', '/admin/power-monitors');
    expect(screen.queryByRole('link', { name: /Farm & Admin Settings/i })).not.toBeInTheDocument();
    powerView.unmount();

    grantOnly('quota:admin');
    const quotasView = renderWorkspace('/admin/settings?scope=system');
    expect(await screen.findByTestId('quotas-page')).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=quotas');
    quotasView.unmount();

    grantOnly('system_settings:admin');
    const profileBoundaryView = renderWorkspace('/admin/settings?tab=slicing&sub=profiles');
    expect(await screen.findByText(/don't have permission to view Slicer Profiles/i)).toBeInTheDocument();
    expect(screen.queryByTestId('slicer-profiles-page')).not.toBeInTheDocument();
    profileBoundaryView.unmount();

    grantOnly();
    renderWorkspace('/admin');
    expect(screen.queryByTestId('admin-hub-attention')).not.toBeInTheDocument();
    expect(screen.getByText(/No operational tools available/i)).toBeInTheDocument();
  });

  it('preserves personal settings separation and blocks dirty workspace search navigation until the user discards', async () => {
    const personalView = renderWorkspace('/settings?scope=system&tab=general&sub=system');
    expect(screen.queryByRole('combobox', { name: 'Search all settings' })).not.toBeInTheDocument();
    expect(screen.getByTestId('theme-switcher')).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('scope=user');
    personalView.unmount();

    renderWorkspace('/admin/settings?scope=system&tab=general&sub=system');
    const retention = await screen.findByLabelText('Retention Days');
    fireEvent.change(retention, { target: { value: '45' } });

    const workspaceSearch = screen.getAllByRole('combobox', { name: 'Search all settings' }).at(-1)!;
    fireEvent.focus(workspaceSearch);
    fireEvent.change(workspaceSearch, { target: { value: 'quotas' } });
    fireEvent.click(await screen.findByRole('option', { name: /Quotas/i }));

    expect(screen.getByRole('dialog', { name: 'Unsaved Changes' })).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('sub=system');

    fireEvent.click(screen.getByRole('button', { name: 'Stay' }));
    expect(screen.queryByRole('dialog', { name: 'Unsaved Changes' })).not.toBeInTheDocument();
    expect(screen.getByLabelText('Retention Days')).toHaveValue(45);

    fireEvent.focus(workspaceSearch);
    fireEvent.change(workspaceSearch, { target: { value: 'quotas' } });
    fireEvent.click(await screen.findByRole('option', { name: /Quotas/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Discard Changes' }));

    await waitFor(() => expect(screen.getByTestId('quotas-page')).toBeInTheDocument());
    expect(screen.getByTestId('location-search')).toHaveTextContent('tab=quotas');
  });

  it('saves per section, reports partial failure, retries only remaining dirty work, and never calls the batch endpoint', async () => {
    let networkAttempts = 0;
    settingsApi.saveSettingsValues.mockImplementation((key: string) => {
      if (key === 'NetworkDiscovery') {
        networkAttempts += 1;
        if (networkAttempts === 1) {
          return Promise.reject(new Error('Network discovery save failed'));
        }
      }
      return Promise.resolve();
    });

    await openSystemSettings();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '45' } });
    fireEvent.change(screen.getByLabelText('Scan Interval Minutes'), { target: { value: '11' } });

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(settingsApi.saveSettingsValues).toHaveBeenCalledTimes(2));
    expect(settingsApi.saveSettingsValues).toHaveBeenNthCalledWith(1, 'SystemLog', {
      enabled: true,
      retentionDays: 45,
    });
    expect(settingsApi.saveSettingsValues).toHaveBeenNthCalledWith(2, 'NetworkDiscovery', {
      enableDiscovery: true,
      scanIntervalMinutes: 11,
    });

    expect(await screen.findByRole('alert')).toHaveTextContent('Saved System Logging. Failed to save Network Discovery');
    expect(screen.getByTestId('admin-save-bar')).toHaveTextContent('1 change in Network Discovery');
    expect(toastMocks.error).toHaveBeenCalledWith('Saved System Logging. Failed to save Network Discovery', undefined);

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(settingsApi.saveSettingsValues).toHaveBeenCalledTimes(3));
    expect(settingsApi.saveSettingsValues).toHaveBeenNthCalledWith(3, 'NetworkDiscovery', {
      enableDiscovery: true,
      scanIntervalMinutes: 11,
    });
    await waitFor(() => expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument());
  });

  it('keeps edits made while a section save is in flight dirty after the saved baseline advances', async () => {
    let resolveSave: (() => void) | undefined;
    settingsApi.saveSettingsValues.mockImplementation(
      () => new Promise<void>((resolve) => {
        resolveSave = resolve;
      }),
    );

    await openSystemSettings();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '45' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '46' } });

    await act(async () => {
      resolveSave?.();
    });

    await waitFor(() => expect(screen.getByLabelText('Retention Days')).toHaveValue(46));
    const saveBar = screen.getByTestId('admin-save-bar');
    expect(within(saveBar).getByText(/1 change in System Logging/i)).toBeInTheDocument();
  });
});
