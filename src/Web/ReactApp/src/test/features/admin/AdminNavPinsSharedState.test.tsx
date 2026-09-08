import React from 'react';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { Layout } from '@/common/components/Layout';
import { AdminControlCenterPage } from '@/features/admin/pages/AdminControlCenterPage';
import { AdminDestinationRoute } from '@/features/admin/components/AdminDestinationRoute';
import { AdminNavPinsProvider } from '@/common/contexts/AdminNavPinsContext';
import { useAdminNavPins } from '@/common/contexts/useAdminNavPins';
import { getNavPreferencesStorageKey, saveNavPreferences, NAV_PREFERENCES_VERSION } from '@/common/utils/navPreferences';
import { createStorageEvent } from '@/test/utils/storage-event';
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
              <Route path="/admin/workers" element={
                <AdminDestinationRoute destinationId="ops-workers">
                  <div>Workers page body</div>
                </AdminDestinationRoute>
              }
              />
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

    // Nothing pinned yet: no extra Admin destination entry beyond the default
    // anchored Admin rail items.
    expect(screen.queryByRole('link', { name: 'Workers & Jobs' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Pin admin links' }));
    await user.click(screen.getByRole('button', { name: 'Pin Workers & Jobs from navbar' }));

    // Same tab, no reload: Layout's navbar must reflect the pin immediately,
    // inside the existing Admin section rather than a top-level Favorites rail.
    const pinnedLink = await screen.findByRole('link', { name: 'Workers & Jobs' });
    const adminRail = screen.getByRole('region', { name: 'Admin' });
    expect(within(adminRail).getByRole('link', { name: 'Workers & Jobs' })).toBe(pinnedLink);
    expect(screen.queryByRole('region', { name: 'Favorites' })).not.toBeInTheDocument();

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

  it('syncs remote admin pin additions and removals across the provider and Layout', async () => {
    const user = userEvent.setup();
    renderShell();
    await screen.findByTestId('admin-hub-operations');
    await user.click(screen.getByRole('button', { name: 'Pin admin links' }));
    const storageKey = getNavPreferencesStorageKey('user-1');

    for (const pinned of [true, false]) {
      const newValue = JSON.stringify({
        version: NAV_PREFERENCES_VERSION,
        orderedItemIds: ['files', 'overview'],
        hiddenItemIds: ['projects'],
        pinnedItemIds: [],
        adminPinnedItemIds: pinned ? ['ops-workers'] : [],
      });
      // Only a native event: the same-tab save helper must not mask a failure.
      localStorage.setItem(storageKey, newValue);
      fireEvent(window, createStorageEvent({ key: storageKey, newValue }));

      expect(pinsRenderLog.at(-1)).toEqual(pinned ? ['ops-workers'] : []);
      expect(screen.getByRole('button', { name: `${pinned ? 'Unpin' : 'Pin'} Workers & Jobs from navbar` }))
        .toBeInTheDocument();
      if (pinned) {
        expect(screen.getByRole('link', { name: 'Workers & Jobs' })).toHaveAttribute('href', '/admin/workers?workerTab=jobs');
      } else {
        expect(screen.queryByRole('link', { name: 'Workers & Jobs' })).not.toBeInTheDocument();
      }
      expect(screen.queryByRole('link', { name: 'Projects' })).not.toBeInTheDocument();
    }
  });

  it.each(['remove', 'clear', 'malformed'])('clears admin pins in every consumer on remote %s', async (reset) => {
    seedPins('user-1', ['ops-analytics']);
    renderShell();
    await screen.findByTestId('admin-hub-operations');
    expect(screen.getByRole('link', { name: 'Analytics' })).toBeInTheDocument();
    const storageKey = getNavPreferencesStorageKey('user-1');

    if (reset === 'clear') localStorage.clear();
    else if (reset === 'remove') localStorage.removeItem(storageKey);
    else localStorage.setItem(storageKey, '{invalid json');
    fireEvent(window, createStorageEvent({
      key: reset === 'clear' ? null : storageKey,
      newValue: localStorage.getItem(storageKey),
    }));

    expect(pinsRenderLog.at(-1)).toEqual([]);
    expect(screen.queryByRole('link', { name: 'Analytics' })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Favorites' })).not.toBeInTheDocument();
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

    // The subscription must move to the new principal's key as well.
    const renderCount = pinsRenderLog.length;
    fireEvent(window, createStorageEvent({ key: getNavPreferencesStorageKey('user-1') }));
    expect(pinsRenderLog).toHaveLength(renderCount);

    const storageKey = getNavPreferencesStorageKey('user-2');
    const newValue = JSON.stringify({
      version: NAV_PREFERENCES_VERSION,
      orderedItemIds: [],
      hiddenItemIds: [],
      pinnedItemIds: [],
      adminPinnedItemIds: ['ops-workers'],
    });
    localStorage.setItem(storageKey, newValue);
    fireEvent(window, createStorageEvent({ key: storageKey, newValue }));
    expect(pinsRenderLog.at(-1)).toEqual(['ops-workers']);
    expect(screen.getByRole('link', { name: 'Workers & Jobs' })).toBeInTheDocument();
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

  it('shows a per-page pin control on admin destination routes and adds the link under Admin immediately', async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/admin/workers']}>
          <AdminNavPinsProvider>
            <Routes>
              <Route element={<Layout />}>
                <Route
                  path="/admin/workers"
                  element={(
                    <AdminDestinationRoute destinationId="ops-workers">
                      <div>Workers page body</div>
                    </AdminDestinationRoute>
                  )}
                />
              </Route>
            </Routes>
          </AdminNavPinsProvider>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    const pagePinButton = screen.getByRole('button', { name: 'Pin Workers & Jobs from navbar' });
    expect(pagePinButton).toHaveAttribute('aria-pressed', 'false');

    await user.click(pagePinButton);

    expect(pagePinButton).toHaveAttribute('aria-pressed', 'true');
    const adminRail = screen.getByRole('region', { name: 'Admin' });
    expect(within(adminRail).getByRole('link', { name: 'Workers & Jobs' })).toHaveAttribute('href', '/admin/workers?workerTab=jobs');
  });
});
