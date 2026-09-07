import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { Layout } from '@/common/components/Layout';
import { AdminControlCenterPage } from '@/features/admin/pages/AdminControlCenterPage';
import type { AdminOverviewDto } from '@/types/adminOverview';

/**
 * Issue 2526 acceptance criterion:
 *
 *   "On /admin, page content has no Admin Home/Control Center self-card,
 *    self-link, or redundant breadcrumb link to /admin. Preserve the global
 *    Admin navigation entry and legitimate parent links on child pages.
 *    Assertions distinguish main content from global navigation."
 *
 * AdminControlCenterPage.test.tsx proves the page's own DOM in isolation, but
 * isolation cannot distinguish two surfaces that are never rendered together.
 * This suite mounts the *real* `Layout` with the *real* hub in its `<Outlet />`
 * so both surfaces exist at once and the distinction is genuinely testable:
 * the global rail keeps its `/admin` entry, `<main>` has none.
 */

// ── Mocks ────────────────────────────────────────────────────────────────────

vi.mock('@/services/api/httpClient', () => ({
  client: { get: vi.fn() },
}));

let mockUserRole = 'farm_admin';
let mockPermissionOverride: ((resource: string, action: string) => boolean) | null = null;

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: {
      id: '1',
      email: 'admin@test.com',
      username: 'admin',
      role: mockUserRole,
      roles: [mockUserRole],
      isActive: true,
    },
    hasRole: (role: string) => role === mockUserRole,
    hasPermission: (resource: string, action: string) =>
      mockPermissionOverride ? mockPermissionOverride(resource, action) : mockUserRole === 'farm_admin',
    error: null,
    login: vi.fn(),
    loginWithPasskey: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
  }),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true, isLoading: false }),
}));

vi.mock('@/common/hooks/useSystemCapabilities', () => ({
  useSystemCapabilities: () => ({
    data: {
      architecture: 'x64',
      slicingEnabled: true,
      modelFilesEnabled: true,
      thumbnailGenerationEnabled: true,
      gcodeUploadEnabled: true,
    },
  }),
}));

vi.mock('@/contexts/ThemeContext', () => ({
  useTheme: () => ({ theme: 'light', setTheme: vi.fn() }),
}));

vi.mock('@/common/hooks/useSignalR', () => ({
  useSignalRConnection: () => ({ isConnected: true }),
  usePrinterStatusUpdates: () => ({ printerStatuses: new Map() }),
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    connect: vi.fn().mockResolvedValue(undefined),
    onPrinterStatusUpdate: vi.fn().mockReturnValue(() => {}),
    onAutoDispatchStateChanged: vi.fn().mockReturnValue(() => {}),
  },
}));

vi.mock('@/features/tasks', () => ({ TasksBadge: () => null }));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAllAutoDispatchStatuses: () => ({ data: [], isLoading: false }),
}));

import { client } from '@/services/api/httpClient';

const mockedApiGet = vi.mocked(client.get);

// ── Fixtures ─────────────────────────────────────────────────────────────────

function makeOverview(): AdminOverviewDto {
  return {
    checkedAt: '2026-07-25T17:04:00Z',
    overallStatus: 'Degraded',
    subsystems: [{ key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' }],
    attention: [
      {
        key: 'self-link',
        severity: 'Warning',
        title: 'An item the backend points at the hub',
        detail: 'Even a backend-supplied /admin target must not become a self-link.',
        actionLabel: 'Open',
        actionRoute: '/admin',
      },
    ],
  } as AdminOverviewDto;
}

function renderAdminRoute() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/admin']}>
        <Routes>
          <Route element={<Layout />}>
            <Route path="/admin" element={<AdminControlCenterPage />} />
          </Route>
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const SELF_LINK_SELECTOR = [
  'a[href="/admin"]',
  'a[href="/admin/"]',
  'a[href^="/admin?"]',
  'a[href^="/admin#"]',
  'a[href^="/admin/?"]',
  'a[href^="/admin/#"]',
].join(', ');

// ── Tests ────────────────────────────────────────────────────────────────────

describe('Admin Control Center in the real app shell (Issue 2526)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUserRole = 'farm_admin';
    mockPermissionOverride = null;
    localStorage.clear();
    mockedApiGet.mockImplementation((url: string) => {
      // Route by URL: the shell renders live chrome widgets that also call the
      // API, and handing them the admin overview shape crashes them.
      if (url === '/admin/overview') {
        return Promise.resolve({ data: makeOverview() });
      }
      return Promise.reject(new Error(`unstubbed GET ${url}`));
    });
  });

  it('keeps the global Admin nav entry while the hub page content has no self-link', async () => {
    const { container } = renderAdminRoute();

    const mainNav = container.querySelector('aside nav[aria-label="Main navigation"]');
    expect(mainNav).not.toBeNull();

    const main = container.querySelector('main');
    expect(main).not.toBeNull();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });

    // Global navigation: the Admin entry is the hub's own default home and must
    // survive. It is a separate surface from page content.
    //
    // Issue 2526 AC: "Preserve the global Admin navigation entry ... Assertions
    // distinguish main content from global navigation." The no-self-link rule is
    // scoped to *page content*; the rail entry stays a live link, exactly as
    // every other rail entry does on its own route (it doubles as the rail
    // collapse toggle), and announces the current location via aria-current.
    const railAdminLinks = mainNav!.querySelectorAll<HTMLAnchorElement>(SELF_LINK_SELECTOR);
    expect(railAdminLinks.length).toBeGreaterThan(0);
    expect(railAdminLinks[0]).toHaveAttribute('aria-current', 'page');

    // Main content: zero self-links, including the backend-supplied attention
    // action above, which resolveAttentionActionRoute must have suppressed.
    expect(main!.querySelectorAll(SELF_LINK_SELECTOR)).toHaveLength(0);
    expect(main!.querySelector('[data-testid="admin-hub-attention-item"] a')).toBeNull();
  });

  it('renders no Admin Home card among the hub destination tiles', async () => {
    renderAdminRoute();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });

    for (const card of screen.getAllByTestId('admin-hub-destination')) {
      expect(card.getAttribute('href')).not.toBe('/admin');
      expect(card.getAttribute('href')).not.toBe('/admin/');
    }
    expect(screen.queryByRole('link', { name: /admin control center/i })).not.toBeInTheDocument();
  });

  it('routes the Control-Center-owned destinations only from the hub, never from the rail', async () => {
    const { container } = renderAdminRoute();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });

    const mainNav = container.querySelector('aside nav[aria-label="Main navigation"]')!;
    const main = container.querySelector('main')!;

    // Positive ownership: the hub is where these live now.
    for (const href of ['/maintenance', '/analytics', '/locations', '/catalog', '/auto-dispatch']) {
      expect(main.querySelectorAll(`a[href="${href}"]`).length).toBeGreaterThan(0);
      expect(mainNav.querySelectorAll(`a[href="${href}"]`)).toHaveLength(0);
    }

    // And nowhere else in the global chrome either (mobile drawer included).
    const chrome = Array.from(container.querySelectorAll('a[href]')).filter(
      (link) => !main.contains(link),
    );
    for (const href of ['/maintenance', '/analytics', '/locations', '/catalog', '/auto-dispatch']) {
      expect(chrome.filter((link) => link.getAttribute('href') === href)).toHaveLength(0);
    }
  });
});
