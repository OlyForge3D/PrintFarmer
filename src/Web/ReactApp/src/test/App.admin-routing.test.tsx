import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Outlet } from 'react-router';

vi.mock('@/common/hooks/useUnifiedLogging', () => ({
  useUnifiedLogging: () => ({ logger: { info: vi.fn(), warn: vi.fn(), error: vi.fn() } }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getSetupStatus: vi.fn().mockResolvedValue({ needsSetup: false }),
  },
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

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: { id: 'user-1', email: 'admin@test.com', role: 'farm_admin', isActive: true },
    hasRole: (role: string) => role === 'farm_admin',
    hasPermission: () => true,
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

import App from '../App';

describe('App canonical admin routes', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it.each([
    '/admin/settings?tab=slicing&sub=profiles',
    '/admin/manage?tab=operations&sub=workers&workerTab=jobs',
  ])('renders the canonical destination without rewriting %s', async (destination) => {
    window.history.pushState({}, '', destination);

    render(<App />);

    expect(await screen.findByText('SettingsShellMock')).toBeInTheDocument();
    await waitFor(() => {
      expect(`${window.location.pathname}${window.location.search}`).toBe(destination);
    });
  });

  it('uses the locations index route for the dashboard default', async () => {
    window.history.pushState({}, '', '/locations');

    render(<App />);

    expect(await screen.findByText('LocationDashboardMock')).toBeInTheDocument();
    await waitFor(() => {
      expect(window.location.pathname).toBe('/locations/dashboard');
    });
  });
});
