import React from 'react';
import { act, render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Outlet } from 'react-router';

const mockUseSystemCapabilities = vi.fn();
const lazyNewSliceJobPageModule = vi.hoisted(() => {
  let releaseImport: () => void;
  let markResolved: () => void;
  const importReleased = new Promise<void>(resolve => {
    releaseImport = resolve;
  });
  const importResolved = new Promise<void>(resolve => {
    markResolved = resolve;
  });

  return {
    releaseImport: () => releaseImport(),
    waitUntilReleased: () => importReleased,
    markResolved: () => markResolved(),
    waitUntilResolved: () => importResolved,
  };
});

vi.mock('@/common/hooks/useUnifiedLogging', () => ({
  useUnifiedLogging: () => ({ logger: { info: vi.fn(), warn: vi.fn(), error: vi.fn() } }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getSetupStatus: vi.fn().mockResolvedValue({ needsSetup: false }),
    // QueueRealtimeBridge mounts with every authenticated <App>. Without these
    // it throws and enters a 100/250/500ms retry loop that races the route
    // assertions below.
    getQueueSubscriptionResources: vi
      .fn()
      .mockResolvedValue({ printerIds: [], jobIds: [], projectIds: [] }),
    getPrinters: vi.fn().mockResolvedValue([]),
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
  useSystemCapabilities: () => mockUseSystemCapabilities(),
}));

vi.mock('@/features/slicer/pages/NewSliceJobPage', async () => {
  await lazyNewSliceJobPageModule.waitUntilReleased();
  lazyNewSliceJobPageModule.markResolved();
  return {
    NewSliceJobPage: () => <div>NewSliceJobPageMock</div>,
  };
});

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

describe('App slicer route consolidation', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUseSystemCapabilities.mockReturnValue({
      data: {
        slicingEnabled: true,
        modelFilesEnabled: true,
        architecture: 'x64',
        platformNote: '',
      },
      error: null,
    });
  });

  it('redirects the old admin slicer profiles route to the settings slicing tab', async () => {
    window.history.pushState({}, '', '/admin/slicer-profiles');

    render(<App />);

    await waitFor(() => {
      expect(window.location.pathname).toBe('/admin/settings');
      expect(window.location.search).toContain('tab=slicing');
    });
  });

  it('redirects the old import-official route to the shared import wizard', async () => {
    window.history.pushState({}, '', '/slicer/import-official');

    render(<App />);

    expect(await screen.findByText('ProfileImportWizardMock')).toBeInTheDocument();
    await waitFor(() => {
      expect(window.location.pathname).toBe('/profiles/import');
    });
  });

  it('does not render a capability-gated route until paused first-load data resolves', async () => {
    mockUseSystemCapabilities.mockReturnValue({
      data: undefined,
      error: null,
      isLoading: false,
      fetchStatus: 'paused',
    });
    window.history.pushState({}, '', '/slicer');

    const { rerender } = render(<App />);

    expect(await screen.findByRole('status', { name: 'Loading platform capabilities' })).toBeInTheDocument();
    expect(screen.queryByText('NewSliceJobPageMock')).not.toBeInTheDocument();

    mockUseSystemCapabilities.mockReturnValue({
      data: {
        slicingEnabled: true,
        modelFilesEnabled: true,
        architecture: 'x64',
        platformNote: '',
      },
      error: null,
    });
    rerender(<App />);

    expect(await screen.findByRole('status', { name: 'Loading' })).toBeInTheDocument();
    expect(screen.queryByRole('status', { name: 'Loading platform capabilities' })).not.toBeInTheDocument();

    // The capability gate is open; synchronize with the separate route-level lazy import.
    await act(async () => {
      lazyNewSliceJobPageModule.releaseImport();
      await lazyNewSliceJobPageModule.waitUntilResolved();
    });

    expect(await screen.findByText('NewSliceJobPageMock')).toBeInTheDocument();
  });

  it('keeps a resolved disabled capability distinct from unresolved data', async () => {
    mockUseSystemCapabilities.mockReturnValue({
      data: {
        slicingEnabled: false,
        modelFilesEnabled: true,
        architecture: 'arm64',
        platformNote: 'Slicing is disabled.',
      },
      error: null,
    });
    window.history.pushState({}, '', '/slicer');

    render(<App />);

    expect(await screen.findByText('Feature Not Available')).toBeInTheDocument();
    expect(screen.queryByText('NewSliceJobPageMock')).not.toBeInTheDocument();
  });
});
