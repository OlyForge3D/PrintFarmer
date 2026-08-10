import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { AdminControlCenterPage } from '@/features/admin/pages/AdminControlCenterPage';
import { resetPageHeaderGuard } from '@/common/components/pageHeaderGuard';
import type { AdminOverviewDto } from '@/types/adminOverview';

/**
 * #1415 — at a 320px viewport, the "Admin Control Center" heading was wider
 * (280px) than its overflow-hidden container (clientWidth 248px), clipping
 * the final 12px of the title. The fix lets this specific heading wrap
 * instead of truncating; other pages built around single-line truncation are
 * unaffected (see PageTemplate.heading.test.tsx).
 *
 * jsdom does not perform real CSS layout, so this test locks in the
 * behavioural contract instead of pixel measurements: at a 320px viewport the
 * full title text is present in the DOM (nothing clipped by `truncate` +
 * `overflow: hidden`), and the heading opts into wrapping rather than
 * ellipsis-clipping.
 */

vi.mock('@/services/api', () => ({
  apiClient: { get: vi.fn() },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: vi.fn(),
}));

import { apiClient } from '@/services/api';
import { useAuth } from '@/features/auth/hooks/useAuth';

const mockedApiGet = vi.mocked(apiClient.get);
const mockedUseAuth = vi.mocked(useAuth);

function makeOverview(overrides: Partial<AdminOverviewDto> = {}): AdminOverviewDto {
  return {
    checkedAt: '2026-07-25T17:04:00Z',
    subsystems: [{ key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' }],
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

function setViewportWidth(width: number) {
  Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: width });
  window.dispatchEvent(new Event('resize'));
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

describe('AdminControlCenterPage — title clipping at 320px (#1415)', () => {
  const originalInnerWidth = window.innerWidth;

  beforeEach(() => {
    resetPageHeaderGuard();
    vi.clearAllMocks();
    mockedUseAuth.mockReturnValue(farmAdminAccess() as unknown as ReturnType<typeof useAuth>);
    mockedApiGet.mockResolvedValue({ data: makeOverview() });
  });

  afterEach(() => {
    setViewportWidth(originalInnerWidth);
  });

  it('renders the full, unclipped title at a 320px viewport', async () => {
    setViewportWidth(320);
    renderHub();

    await waitFor(() => {
      expect(screen.queryByLabelText('Loading system health')).not.toBeInTheDocument();
    });

    const h1 = screen.getByRole('heading', { level: 1, name: 'Admin Control Center' });
    // The complete title text must be present — nothing truncated off the end.
    expect(h1.textContent).toBe('Admin Control Center');
    // Wrapping, not clipping: no ellipsis-truncation class on this heading.
    expect(h1.className).not.toContain('truncate');
    expect(h1.className).toContain('whitespace-normal');
    expect(h1.className).toContain('break-words');
  });

  it('keeps the same wrapping treatment at 390px and wider (unchanged, not narrower-only)', async () => {
    setViewportWidth(390);
    renderHub();

    await waitFor(() => {
      expect(screen.queryByLabelText('Loading system health')).not.toBeInTheDocument();
    });

    const h1 = screen.getByRole('heading', { level: 1, name: 'Admin Control Center' });
    expect(h1.textContent).toBe('Admin Control Center');
    expect(h1.className).not.toContain('truncate');
  });
});
