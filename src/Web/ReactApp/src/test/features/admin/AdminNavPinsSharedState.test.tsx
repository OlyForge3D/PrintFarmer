import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { Layout } from '@/common/components/Layout';
import { AdminControlCenterPage } from '@/features/admin/pages/AdminControlCenterPage';
import { AdminNavPinsProvider } from '@/common/contexts/AdminNavPinsContext';
import { useAdminNavPins } from '@/common/contexts/useAdminNavPins';
import { getNavPreferencesStorageKey, saveNavPreferences, NAV_PREFERENCES_VERSION } from '@/common/utils/navPreferences';
import type { AdminOverviewDto } from '@/types/adminOverview';

/**
 * Issue 2527 acceptance criteria this suite exists specifically to prove,
 * because they were flagged during review (Bishop, Hicks) as either
 * incorrect or untested against the *real* app shell:
 *
 *   1. A pinned destination navigates to the same canonical target its own
 *      card/tile would use (regression: `Layout` read the raw registry path
 *      and lost the `ops-workers` → `?workerTab=jobs` override).
 *   2. A pin toggled in the Control Center appears in `Layout`'s navbar in
 *      the same tab, without a reload (shared state, not just storage).
 *   3. Logout / account switch never renders the previous principal's pins,
 *      not even for a single transient frame.
 *   4. A pin whose destination the user has since lost access to must not
 *      render, even though it is still present in storage.
 *   5. "Reset to defaults" (an unrelated navbar customization action) must
 *      not silently erase admin pins.
 *
 * Mounts the *real* `Layout` with the *real* `AdminControlCenterPage` in its
 * `<Outlet />`, both under the *real* `AdminNavPinsProvider`, matching how
 * `App.tsx` wires them — an isolated-component test cannot show these two
 * surfaces failing to agree, or failing to stay in sync.
 */

// ── Mocks ────────────────────────────────────────────────────────────────────

vi.mock('@/services/api/httpClient', () => ({
  client: { get: vi.fn() },
}));

let mockUserId = 'user-1';
let mockUserRole = 'farm_admin';
let mockPermissionOverride: ((resource: string, action: string) => boolean) | null = null;

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: {
      id: mockUserId,
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
    overallStatus: 'Healthy',
    subsystems: [{ key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' }],
    attention: [],
  } as AdminOverviewDto;
}

function shellElement(queryClient: QueryClient) {
  return (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/admin']}>
        <AdminNavPinsProvider>
          <NavPinsRenderProbe />
          <Routes>
            <Route element={<Layout />}>
              <Route path="/admin" element={<AdminControlCenterPage />} />
            </Route>
          </Routes>
        </AdminNavPinsProvider>
      </MemoryRouter>
    </QueryClientProvider>
  );
}

// Records `pinnedIds` on every render of a real consumer inside the real
// `AdminNavPinsProvider`. `screen`-based assertions taken after `rerender()`
// only see the DOM once React Testing Library's `act()` wrapper has flushed
// every effect, which hides a stale-then-corrected render that a real browser
// would paint for one frame. This probe observes every render pass, including
// ones `act()` flushes before an assertion could otherwise see them, so it can
// prove no render — not just the final one — ever exposed another
// principal's pins.
let pinsRenderLog: string[][] = [];

function NavPinsRenderProbe() {
  const { pinnedIds } = useAdminNavPins();
  pinsRenderLog.push(pinnedIds);
  return null;
}

function renderShell() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });

  const result = render(shellElement(queryClient));
  return { ...result, queryClient };
}

function seedPins(userId: string, ids: string[]) {
  const storageKey = getNavPreferencesStorageKey(userId);
  saveNavPreferences(storageKey, {
    version: NAV_PREFERENCES_VERSION,
    orderedItemIds: [],
    hiddenItemIds: [],
    pinnedItemIds: [],
    adminPinnedItemIds: ids,
  });
}

describe('Admin nav pins: shared state across Control Center and Layout (Issue 2527)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    pinsRenderLog = [];
    mockUserId = 'user-1';
    mockUserRole = 'farm_admin';
    mockPermissionOverride = null;
    localStorage.clear();
    mockedApiGet.mockImplementation((url: string) => {
      if (url === '/admin/overview') {
        return Promise.resolve({ data: makeOverview() });
      }
      return Promise.reject(new Error(`unstubbed GET ${url}`));
    });
  });

  it('pins a destination with a path override and navigates to the same canonical target its own card uses, live in the same tab', async () => {
    const user = userEvent.setup();
    renderShell();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });

    // Nothing pinned yet: no Favorites section, no Workers & Jobs navbar link.
    expect(screen.queryByRole('region', { name: 'Favorites' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Pin admin links' }));
    await user.click(screen.getByRole('button', { name: 'Pin Workers & Jobs from navbar' }));

    // Same tab, no reload: Layout's navbar must reflect the pin immediately.
    const pinnedLink = await screen.findByRole('link', { name: 'Workers & Jobs' });
    expect(screen.getByRole('region', { name: 'Favorites' })).toBeInTheDocument();

    // The regression under test: Layout must resolve the same override the
    // dashboard's own Workers & Jobs card uses (`?workerTab=jobs`), not the
    // registry's bare path. Confirm the dashboard card agrees, so this test
    // cannot pass by coincidentally hardcoding the wrong href on both sides.
    const dashboardCard = screen.getByTestId('admin-hub-operations').querySelector('a[href^="/admin/workers"]');
    expect(dashboardCard).not.toBeNull();
    expect(pinnedLink).toHaveAttribute('href', dashboardCard!.getAttribute('href'));
    expect(pinnedLink).toHaveAttribute('href', '/admin/workers?workerTab=jobs');

    // Accessibility: a navbar link must never contain a nested interactive
    // button (found earlier in this epic as a real defect elsewhere).
    expect(pinnedLink.querySelector('button')).toBeNull();
  });

  it('renders no pins for a new principal even transiently, when switching accounts', async () => {
    seedPins('user-1', ['ops-analytics']);

    const { rerender } = renderShell();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });
    expect(await screen.findByRole('link', { name: 'Analytics' })).toBeInTheDocument();
    // Sanity: the probe actually observed user-1's pin before the switch, so
    // the assertion below is testing something real rather than vacuously
    // passing because the pin never rendered at all.
    expect(pinsRenderLog.some((ids) => ids.includes('ops-analytics'))).toBe(true);
    const switchLogIndex = pinsRenderLog.length;

    // Switch principal — user-2 has never pinned anything.
    mockUserId = 'user-2';
    rerender(shellElement(new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } } })));

    // No transient frame may show user-1's "Analytics" pin under user-2 — not
    // just the final, post-effect DOM (see NavPinsRenderProbe above), but
    // *every* render this context produced from the moment the switch began.
    expect(pinsRenderLog.slice(switchLogIndex).every((ids) => !ids.includes('ops-analytics'))).toBe(true);
    expect(screen.queryByRole('region', { name: 'Favorites' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Analytics' })).not.toBeInTheDocument();
  });

  it('suppresses a stale pin once the user has lost access to its destination, even though it remains in storage', async () => {
    seedPins('user-1', ['ops-workers']);

    const { rerender } = renderShell();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });
    // Still authorized: the stale-looking pin renders normally at first.
    expect(await screen.findByRole('link', { name: 'Workers & Jobs' })).toBeInTheDocument();

    // The stored pin ID never changes, but access to `dispatch-settings:manage`
    // is revoked (e.g. a role/permission change elsewhere). A stored ID must
    // never itself confer permission.
    mockPermissionOverride = (resource) => resource !== 'dispatch-settings';
    rerender(shellElement(new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } } })));

    await waitFor(() => {
      expect(screen.queryByRole('link', { name: 'Workers & Jobs' })).not.toBeInTheDocument();
    });
  });

  it('preserves admin pins when "Reset to defaults" clears regular navbar customization', async () => {
    seedPins('user-1', ['ops-analytics']);
    const user = userEvent.setup();
    renderShell();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });
    expect(await screen.findByRole('link', { name: 'Analytics' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Customize navigation' }));
    await user.click(screen.getByRole('button', { name: 'Reset to defaults' }));

    // The admin pin must survive a reset of the *regular* navbar preferences.
    expect(screen.getByRole('link', { name: 'Analytics' })).toBeInTheDocument();
  });
});
