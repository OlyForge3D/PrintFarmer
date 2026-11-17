import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Outlet } from 'react-router';

const mockUseAuth = vi.hoisted(() => vi.fn());
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
  useAuth: mockUseAuth,
}));

vi.mock('@/features/auth/components/ProtectedRoute', () => ({
  ProtectedRoute: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/features/auth/pages/LoginPage', () => ({
  LoginPage: () => <div>LoginPageMock</div>,
}));

vi.mock('@/features/auth/pages/ForgotPasswordPage', () => ({
  ForgotPasswordPage: () => <div>ForgotPasswordPageMock</div>,
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
    mockUseAuth.mockReturnValue({
      isAuthenticated: false,
      isLoading: false,
      user: null,
      login: vi.fn(),
      loginWithPasskey: vi.fn(),
      register: vi.fn(),
      hasRole: () => false,
      hasPermission: () => false,
    });
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

  it.each([
    ['/forgot-password', 'ForgotPasswordPageMock'],
    ['/reset-password?token=synthetic-token', 'ResetPasswordPageMock'],
  ])('allows signed-out users to open %s', async (path, pageName) => {
    window.history.pushState({}, '', path);

    render(<App />);

    expect(await screen.findByText(pageName)).toBeInTheDocument();
    expect(window.location.pathname).toBe(path.split('?')[0]);
  });

  it('continues redirecting signed-out users from protected routes to sign-in', async () => {
    window.history.pushState({}, '', '/printQueue/history?source=printers');

    render(<App />);

    expect(await screen.findByText('LoginPageMock')).toBeInTheDocument();
    await waitFor(() => expect(window.location.pathname).toBe('/login'));
  });
});
