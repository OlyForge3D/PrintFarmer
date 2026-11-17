import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Outlet } from 'react-router';

const mockAuthState = vi.hoisted(() => ({
  isAuthenticated: false,
  isLoading: false,
  user: null as { id: string; isActive: boolean } | null,
  login: vi.fn(),
  loginWithPasskey: vi.fn(),
  register: vi.fn(),
  hasRole: vi.fn(() => false),
  hasPermission: vi.fn(() => false),
}));
const mockUseSystemCapabilities = vi.fn();

vi.mock('@/common/hooks/useUnifiedLogging', () => ({
  useUnifiedLogging: () => ({ logger: { info: vi.fn(), warn: vi.fn(), error: vi.fn() } }),
}));

vi.mock('@/services/api/setupApi', () => ({
  getSetupStatus: vi.fn().mockResolvedValue({ needsSetup: false }),
}));

vi.mock('@/services/api/queueRealtimeApi', () => ({
  getQueueSubscriptionResources: vi
    .fn()
    .mockResolvedValue({ printerIds: [], jobIds: [], projectIds: [] }),
}));

vi.mock('@/services/api/printerApi', () => ({
  getPrinters: vi.fn().mockResolvedValue([]),
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

vi.mock('@/common/components/QueueRealtimeBridge', () => ({
  QueueRealtimeBridge: () => null,
}));

vi.mock('@/features/queue/pages/PrintQueueDashboardPage', () => ({
  PrintQueueDashboardPage: () => <div>QueueTarget</div>,
}));

vi.mock('@/features/files/pages/FilesPage', () => ({
  FilesPage: () => <div>FilesTarget</div>,
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
  useAuth: () => mockAuthState,
}));

vi.mock('@/features/auth/components/ProtectedRoute', () => ({
  ProtectedRoute: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/features/auth/pages/ResetPasswordPage', () => ({
  ResetPasswordPage: () => <div>ResetPasswordPageMock</div>,
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

vi.mock('sonner', () => ({
  Toaster: () => null,
  toast: {
    error: vi.fn(),
    warning: vi.fn(),
  },
}));

import App from '../App';

describe('App unauthenticated auth routes', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.history.pushState({}, '', '/');
    mockAuthState.isAuthenticated = false;
    mockAuthState.user = null;
    mockAuthState.login.mockReset().mockResolvedValue(false);
    mockAuthState.loginWithPasskey.mockReset().mockResolvedValue(false);
    mockAuthState.register.mockReset().mockResolvedValue(false);
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

  it('allows signed-out users to open a reset-password link', async () => {
    const path = '/reset-password?token=synthetic-token';
    window.history.pushState({}, '', path);

    render(<App />);

    expect(await screen.findByText('ResetPasswordPageMock')).toBeInTheDocument();
    expect(window.location.pathname).toBe(path.split('?')[0]);
  });

  it('opens the recovery form through the real forgot-password link', async () => {
    window.history.pushState({}, '', '/login');

    render(<App />);
    fireEvent.click(await screen.findByRole('link', { name: /forgot password\?/i }));

    expect(await screen.findByPlaceholderText('your.email@example.com')).toBeInTheDocument();
    expect(window.location.pathname).toBe('/forgot-password');
  });

  it('continues redirecting signed-out users from protected routes to sign-in', async () => {
    window.history.pushState({}, '', '/printQueue/history?source=printers');

    render(<App />);

    expect(await screen.findByRole('heading', { name: 'Sign In' })).toBeInTheDocument();
    await waitFor(() => expect(window.location.pathname).toBe('/login'));
  });

  it('returns to the requested path and query after a successful password sign-in', async () => {
    mockAuthState.login.mockImplementation(async () => {
      mockAuthState.isAuthenticated = true;
      return true;
    });
    window.history.pushState({}, '', '/printQueue/history?source=printers');

    render(<App />);
    fireEvent.change(await screen.findByLabelText('Username or Email'), {
      target: { value: 'operator' },
    });
    fireEvent.change(screen.getByLabelText('Password'), {
      target: { value: 'synthetic-password' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Sign In' }));

    expect(await screen.findByText('QueueTarget')).toBeInTheDocument();
    await waitFor(() => {
      expect(`${window.location.pathname}${window.location.search}`).toBe(
        '/printQueue/history?source=printers',
      );
    });
  });

  it('returns to the requested path and query after a successful passkey sign-in', async () => {
    mockAuthState.loginWithPasskey.mockImplementation(async () => {
      mockAuthState.isAuthenticated = true;
      return true;
    });
    window.history.pushState({}, '', '/files?source=printers');

    render(<App />);
    fireEvent.change(await screen.findByLabelText('Username or Email'), {
      target: { value: 'operator' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in with passkey' }));

    expect(await screen.findByText('FilesTarget')).toBeInTheDocument();
    await waitFor(() => {
      expect(`${window.location.pathname}${window.location.search}`).toBe('/files?source=printers');
    });
  });
});
