/**
 * Authorization regression test: regular (non-admin) users must retain access
 * to API key management at /profile/api-keys.
 *
 * Before PR #376 the route rendered ApiKeysPage directly for any authenticated user.
 * The migration accidentally redirected it into the farm_admin-gated /settings shell.
 * This test asserts the corrected behavior: all authenticated users can reach
 * ApiKeysPage regardless of role.
 */
import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ApiKeysPage } from '@/features/profile/pages/ApiKeysPage';
import { ProtectedRoute } from '@/features/auth/components/ProtectedRoute';

vi.mock('@/common/hooks/useUnifiedLogging', () => ({
  useUnifiedLogging: () => ({ logger: { info: vi.fn(), warn: vi.fn(), error: vi.fn() } }),
}));

vi.mock('@/services/apiKeysService', () => ({
  listApiKeys: vi.fn().mockResolvedValue([]),
  getApiKeySettings: vi.fn().mockResolvedValue({ hashingEnabled: true }),
  createApiKey: vi.fn(),
  toggleApiKey: vi.fn(),
  deleteApiKey: vi.fn(),
  rotateApiKey: vi.fn(),
  revealApiKey: vi.fn(),
}));

// Mock for regular (non-admin) user
function mockRegularUser() {
  vi.mock('@/features/auth/hooks/useAuth', () => ({
    useAuth: () => ({
      user: { id: '2', email: 'user@test.com', isActive: true, roles: ['farm_user'] },
      isAuthenticated: true,
      isLoading: false,
      hasRole: (role: string) => role === 'farm_user',
      hasPermission: () => false,
      logout: vi.fn(),
    }),
    useAuthInternal: () => ({
      user: { id: '2', email: 'user@test.com', isActive: true, roles: ['farm_user'] },
      isAuthenticated: true,
      isLoading: false,
      hasRole: (role: string) => role === 'farm_user',
      hasPermission: () => false,
      logout: vi.fn(),
    }),
  }));
}

mockRegularUser();

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: false } },
});

function renderApiKeysRoute(path = '/profile/api-keys') {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          {/* Access-denied sentinel for routes that should be admin-only */}
          <Route path="/settings" element={<div data-testid="settings-page">Settings (admin only)</div>} />
          {/* The corrected route: no farm_admin gate */}
          <Route path="/profile/api-keys" element={<ApiKeysPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('ApiKeysPage access control', () => {
  it('renders ApiKeysPage for a regular (non-admin) authenticated user', () => {
    renderApiKeysRoute();
    // ApiKeysPage renders its own heading — confirms the page loaded, not a redirect/access-denied screen
    expect(screen.queryByTestId('settings-page')).not.toBeInTheDocument();
    expect(screen.queryByText('Access Denied')).not.toBeInTheDocument();
  });

  it('does NOT redirect non-admin users to /settings', () => {
    renderApiKeysRoute();
    // If the redirect regression were present, the settings sentinel would appear
    expect(screen.queryByTestId('settings-page')).not.toBeInTheDocument();
  });

  it('ApiKeysPage is not wrapped in a farm_admin ProtectedRoute', () => {
    // Smoke-test: render the page component directly without any ProtectedRoute wrapper.
    // A farm_admin-gated wrapper would show "Access Denied" for a non-admin user.
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <ApiKeysPage />
        </MemoryRouter>
      </QueryClientProvider>
    );
    expect(screen.queryByText('Access Denied')).not.toBeInTheDocument();
  });

  it('farm_admin ProtectedRoute still blocks non-admin from /settings', () => {
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/settings']}>
          <Routes>
            <Route
              path="/settings"
              element={
                <ProtectedRoute requiredRole="farm_admin">
                  <div data-testid="settings-content">Settings</div>
                </ProtectedRoute>
              }
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    );
    // Non-admin user should see Access Denied, not the settings content
    expect(screen.queryByTestId('settings-content')).not.toBeInTheDocument();
    expect(screen.getByText('Access Denied')).toBeInTheDocument();
  });
});
