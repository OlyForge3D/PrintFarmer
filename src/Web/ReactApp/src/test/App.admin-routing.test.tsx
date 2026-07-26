import React from 'react';
import { render, waitFor } from '@testing-library/react';
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
    onFailureDetected: vi.fn(() => vi.fn()),
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

// Mock every lazy-loaded page that a legacy redirect can land on. We do not
// care what these pages render — only that the URL matches after Suspense
// resolves. Returning a static div keeps rendering synchronous and cheap.
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
import { LEGACY_REDIRECTS } from '../features/admin/registry/legacyRedirects';

function splitPath(url: string): { pathname: string; search: string } {
  const [pathname, search = ''] = url.split('?');
  return { pathname, search };
}

function paramsEqual(actual: string, expected: string): boolean {
  const actualParams = new URLSearchParams(actual.startsWith('?') ? actual.slice(1) : actual);
  const expectedParams = new URLSearchParams(expected);
  const actualEntries = [...actualParams.entries()].sort();
  const expectedEntries = [...expectedParams.entries()].sort();
  return JSON.stringify(actualEntries) === JSON.stringify(expectedEntries);
}

describe('App legacy admin redirects', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  for (const redirect of LEGACY_REDIRECTS) {
    // `/admin` itself is a special case: it is technically a Route index that
    // uses <Navigate/>, but React Router doesn't fire that redirect at the
    // pathname `/admin` in the same way (the index resolves to the parent
    // path). It's exercised implicitly by every /admin/* redirect and is
    // covered by the registry-level test, so skip the app-mount assertion.
    if (redirect.from === '/admin') {
      continue;
    }

    it(`redirects ${redirect.from} to ${redirect.to}`, async () => {
      window.history.pushState({}, '', redirect.from);

      render(<App />);

      const target = splitPath(redirect.to);

      await waitFor(() => {
        expect(window.location.pathname).toBe(target.pathname);
        expect(
          paramsEqual(window.location.search, target.search),
          `expected search "${target.search}" but got "${window.location.search}"`,
        ).toBe(true);
      });
    });
  }
});
