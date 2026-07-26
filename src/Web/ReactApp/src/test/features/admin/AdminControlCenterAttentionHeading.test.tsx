import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { AdminControlCenterPage } from '@/features/admin/pages/AdminControlCenterPage';
import type { AdminOverviewDto } from '@/types/adminOverview';

/**
 * Epic #939 — the Admin Control Center's "Needs attention" band was
 * previously suppressed on error to avoid rendering an orphan heading with
 * nothing beneath it. That fix landed in #936. This suite locks it in:
 * on error the entire section (heading + body) must disappear, and on
 * success the heading must always be present.
 */

vi.mock('@/services/api', () => ({
  apiClient: { get: vi.fn() },
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

import { apiClient } from '@/services/api';
import { useAuth } from '@/features/auth/hooks/useAuth';

const mockedApiGet = vi.mocked(apiClient.get);
const mockedUseAuth = vi.mocked(useAuth);

function makeOverview(overrides: Partial<AdminOverviewDto> = {}): AdminOverviewDto {
  return {
    checkedAt: '2026-07-25T17:04:00Z',
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

  it('does NOT render the "Needs attention" heading when the overview fetch errors', async () => {
    mockedApiGet.mockRejectedValue(new Error('network down'));

    renderHub();

    // AdminError takes over the top-level fallback.
    await waitFor(() => {
      expect(
        screen.getByRole('heading', { name: "Couldn't load the admin overview" }),
      ).toBeInTheDocument();
    });

    // The whole attention section — heading, body, empty-state — is gone.
    expect(
      screen.queryByRole('heading', { name: 'Needs attention' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByTestId('admin-hub-attention')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('heading', { name: 'Nothing needs your attention' }),
    ).not.toBeInTheDocument();
  });

  it('does render the "Needs attention" heading on a successful load, even with zero items', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview({ attention: [] }) });

    renderHub();

    // Wait for the query to resolve past the loading skeleton.
    await waitFor(() => {
      expect(screen.queryByLabelText('Loading attention items')).not.toBeInTheDocument();
    });

    // Success path shows the empty state under the heading — both present.
    expect(
      screen.getByRole('heading', { name: 'Needs attention' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Nothing needs your attention' }),
    ).toBeInTheDocument();
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
