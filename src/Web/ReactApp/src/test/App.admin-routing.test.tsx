import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Outlet } from 'react-router';

vi.mock('@/common/hooks/useUnifiedLogging', () => ({
  useUnifiedLogging: () => ({ logger: { info: vi.fn(), warn: vi.fn(), error: vi.fn() } }),
}));

vi.mock('@/services/api/setupApi', () => ({
  getSetupStatus: vi.fn().mockResolvedValue({ needsSetup: false }),
}));

vi.mock('@/services/assetService', () => ({
  assetService: {
    initialize: vi.fn(() => Promise.resolve()),
  },
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
  signalRService: {
    connect: vi.fn().mockResolvedValue(undefined),
  },
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

const access = vi.hoisted(() => ({
  admin: true,
  grant: '',
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: { id: 'user-1', email: 'admin@test.com', role: 'farm_admin', isActive: true },
    hasRole: (role: string) => access.admin && role === 'farm_admin',
    hasPermission: (resource: string, action: string) => access.admin || access.grant === `${resource}:${action}`,
  }),
}));

vi.mock('@/features/auth/components/ProtectedRoute', () => ({
  ProtectedRoute: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/features/auth/components/SetupWizard', () => ({
  SetupWizard: () => <div>SetupWizardMock</div>,
}));

vi.mock('@/common/components/ErrorBoundary', () => ({
  ErrorBoundary: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/common/components/Layout', () => ({
  Layout: () => <Outlet />,
}));

vi.mock('@/common/hooks/useSystemCapabilities', () => ({
  useSystemCapabilities: () => ({
    data: {
      slicingEnabled: true,
      modelFilesEnabled: true,
      architecture: 'x64',
      platformNote: '',
    },
  }),
}));

// Mock the lazy-loaded canonical destinations. We only need to confirm that the
// URL remains stable after Suspense resolves, so static content keeps rendering
// synchronous and cheap.
vi.mock('@/features/settings/pages/SettingsShell', () => ({
  SettingsShell: () => <div>SettingsShellMock</div>,
}));

vi.mock('@/features/printers/pages/PrintersPage', () => ({
  PrintersPage: () => <div>PrintersPageMock</div>,
}));

vi.mock('@/features/locations/pages/LocationDashboardPage', () => ({
  LocationDashboardPage: () => <div>LocationDashboardMock</div>,
}));

vi.mock('@/features/analytics/pages/AnalyticsHubPage', () => ({
  AnalyticsHubPage: () => <div>AnalyticsHubMock</div>,
}));

vi.mock('@/features/projects/pages/ProjectsPage', () => ({
  ProjectsPage: () => <div>ProjectsPageMock</div>,
}));

vi.mock('@/features/tasks', () => ({
  ProfileImportWizardPage: () => <div>ProfileImportWizardMock</div>,
  TasksBadge: () => null,
}));

vi.mock('sonner', () => ({
  Toaster: () => null,
  toast: {
    error: vi.fn(),
    warning: vi.fn(),
  },
}));

vi.mock('@/features/system/pages/SystemStatusPage', () => ({
  SystemStatusPage: () => <div>SystemStatusMock</div>,
}));
vi.mock('@/features/slicer/pages/WorkerManagementPage', () => ({
  WorkerManagementPage: ({ embedded, tabQueryParamName }: { embedded?: boolean; tabQueryParamName?: string }) => (
    <div data-testid="worker-page" data-embedded={embedded} data-tab-param={tabQueryParamName}>WorkersMock</div>
  ),
}));
vi.mock('@/features/admin/pages/LoginAuditPage', () => ({
  LoginAuditPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="audit-page" data-embedded={embedded}>LoginAuditMock</div>,
}));
vi.mock('@/features/admin/pages/DataManagementPage', () => ({
  DataManagementPage: ({ embedded }: { embedded?: boolean }) => <div data-testid="data-page" data-embedded={embedded}>DataManagementMock</div>,
}));

import App from '../App';

describe('App canonical admin routes', () => {
  beforeEach(() => {
    access.admin = true;
    access.grant = '';
    vi.clearAllMocks();
  });

  it.each([
    ['/admin/settings?tab=slicing&sub=profiles', 'SettingsShellMock'],
    ['/admin/workers?workerTab=jobs', 'WorkersMock'],
    ['/admin/status', 'SystemStatusMock'],
    ['/admin/login-audit', 'LoginAuditMock'],
    ['/admin/data-management', 'DataManagementMock'],
  ])('renders the canonical destination without rewriting %s', async (destination, content) => {
    window.history.pushState({}, '', destination);

    render(<App />);

    expect(await screen.findByText(content)).toBeInTheDocument();
    await waitFor(() => {
      expect(`${window.location.pathname}${window.location.search}`).toBe(destination);
    });
  });

  it.each([
    ['/admin/status', 'system_settings:admin', 'SystemStatusMock'],
    ['/admin/workers?workerTab=jobs', 'dispatch-settings:manage', 'WorkersMock'],
    ['/admin/login-audit', 'system_settings:admin', 'LoginAuditMock'],
    ['/admin/data-management', 'data_management:admin', 'DataManagementMock'],
  ])('allows the resource delegate but keeps denied query subtrees unmounted: %s', async (path, grant, content) => {
    access.admin = false;
    window.history.pushState({}, '', path);
    const mounted = render(<App />);
    expect(await screen.findByText('Access Denied')).toBeInTheDocument();
    expect(screen.queryByText(content)).not.toBeInTheDocument();
    mounted.unmount();
    access.grant = grant;
    render(<App />);
    expect(await screen.findByText(content)).toBeInTheDocument();
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
    expect(screen.getAllByRole('link', { name: 'Admin Control Center' })).toHaveLength(1);
  });

  it('preserves the worker page query ownership and embedded frame', async () => {
    window.history.pushState({}, '', '/admin/workers?workerTab=jobs');
    render(<App />);
    expect(await screen.findByTestId('worker-page')).toHaveAttribute('data-tab-param', 'workerTab');
    expect(screen.getByTestId('worker-page')).toHaveAttribute('data-embedded', 'true');
  });

  it('does not register or redirect the retired manage route', async () => {
    window.history.pushState({}, '', '/admin/manage');
    render(<App />);
    await screen.findByText(/page not found/i);
    expect(window.location.pathname).toBe('/admin/manage');
    expect(screen.queryByText('SettingsShellMock')).not.toBeInTheDocument();
  });

  it('uses the locations index route for the dashboard default', async () => {
    window.history.pushState({}, '', '/locations');

    render(<App />);

    expect(await screen.findByText('LocationDashboardMock')).toBeInTheDocument();
    await waitFor(() => {
      expect(window.location.pathname).toBe('/locations');
    });
  });
});
