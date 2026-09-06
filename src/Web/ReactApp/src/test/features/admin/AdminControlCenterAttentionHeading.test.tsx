import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { AdminControlCenterPage } from '@/features/admin/pages/AdminControlCenterPage';
import type { AdminOverviewDto } from '@/types/adminOverview';

/**
 * The attention band is the first overview surface. It must remain visible
 * while loading and after a failed fetch so operators get an honest retry
 * state instead of a blank or misleading dashboard.
 */

vi.mock('@/services/api/httpClient', () => ({
  client: { get: vi.fn() },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: vi.fn(),
}));

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({
    title,
    subtitle,
    actions,
    children,
  }: {
    title: string;
    subtitle?: string;
    actions?: React.ReactNode;
    children: React.ReactNode;
  }) => (
    <div data-testid="page-template">
      <h1>{title}</h1>
      {subtitle && <p>{subtitle}</p>}
      {actions && <div data-testid="page-template-actions">{actions}</div>}
      {children}
    </div>
  ),
}));

import { client } from '@/services/api/httpClient';
import { useAuth } from '@/features/auth/hooks/useAuth';

const mockedApiGet = vi.mocked(client.get);
const mockedUseAuth = vi.mocked(useAuth);

function makeOverview(overrides: Partial<AdminOverviewDto> = {}): AdminOverviewDto {
  return {
    checkedAt: '2026-07-25T17:04:00Z',
    overallStatus: 'Healthy',
    subsystems: [
      { key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' },
    ],
    attention: [],
    ...overrides,
  };
}

function farmAdminAccess() {
  return {
    isAuthenticated: true,
    isLoading: false,
    user: {
      id: 'user-1',
      email: 'admin@test.com',
      roles: ['farm_admin'],
      isActive: true,
    },
    hasRole: (role: string) => role === 'farm_admin',
    hasPermission: () => true,
    error: null,
    login: vi.fn(),
    loginWithPasskey: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
  };
}

function renderHub() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/admin']}>
        <AdminControlCenterPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('AdminControlCenterPage — attention heading orphan-suppression (#939)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedUseAuth.mockReturnValue(
      farmAdminAccess() as unknown as ReturnType<typeof useAuth>,
    );
  });

  it('renders the "Needs attention" heading and retry state when the overview fetch errors', async () => {
    mockedApiGet.mockRejectedValue(new Error('network down'));

    renderHub();

    // AdminError takes over the top-level fallback.
    await waitFor(() => {
      expect(
        screen.getByRole('heading', { name: "Couldn't load the admin overview" }),
      ).toBeInTheDocument();
    });

    expect(screen.getByRole('heading', { name: 'Needs attention' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /try again/i })).toBeInTheDocument();
  });

  it('does render the "Needs attention" heading on a successful load, even with zero items', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview({ attention: [] }) });

    renderHub();

    // Wait for the query to resolve past the loading skeleton.
    await waitFor(() => {
      expect(screen.queryByLabelText('Loading attention items')).not.toBeInTheDocument();
    });

    // Success path shows the all-clear line under the heading — both present.
    expect(
      screen.getByRole('heading', { name: 'Needs attention' }),
    ).toBeInTheDocument();
    expect(screen.getByTestId('admin-hub-attention-clear')).toBeInTheDocument();
  });

  it('renders the attention heading and list when items are present', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'p1',
            severity: 'Warning',
            title: 'Printer offline',
            detail: 'printer-01 stopped responding.',
            actionLabel: null,
            actionRoute: null,
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.queryByLabelText('Loading attention items')).not.toBeInTheDocument();
    });

    expect(
      screen.getByRole('heading', { name: 'Needs attention' }),
    ).toBeInTheDocument();
    expect(screen.getByTestId('admin-hub-attention')).toBeInTheDocument();
    expect(screen.getByText('Printer offline')).toBeInTheDocument();
  });
});
