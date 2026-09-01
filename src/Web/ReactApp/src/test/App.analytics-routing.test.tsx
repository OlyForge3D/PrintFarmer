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

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: { id: 'user-1', email: 'admin@test.com', role: 'farm_admin', isActive: true },
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

vi.mock('@/features/tasks', () => ({
  ProfileImportWizardPage: () => <div>ProfileImportWizardMock</div>,
  TasksBadge: () => null,
}));

vi.mock('@/features/analytics/pages/AnalyticsHubPage', () => ({
  AnalyticsHubPage: () => <div>AnalyticsHubMock</div>,
}));

vi.mock('sonner', () => ({
  Toaster: () => null,
  toast: {
    error: vi.fn(),
    warning: vi.fn(),
  },
}));

import App from '../App';

describe('App canonical analytics route', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders the unified analytics production lens without rewriting it', async () => {
    window.history.pushState({}, '', '/analytics?lens=production');

    render(<App />);

    expect(await screen.findByText('AnalyticsHubMock')).toBeInTheDocument();
    await waitFor(() => {
      expect(window.location.pathname).toBe('/analytics');
      expect(window.location.search).toBe('?lens=production');
    });
  });
});
