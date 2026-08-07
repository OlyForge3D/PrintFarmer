import { beforeAll, beforeEach, describe, it, expect, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  ADMIN_DESTINATIONS,
  ADMIN_HUB_PARENT,
} from '@/features/admin/registry/adminDestinations';
import { PowerMonitorSettingsPage } from '@/features/power-monitors/components/PowerMonitorSettingsPage';
import { SettingsShell } from '@/features/settings/pages/SettingsShell';
import { resolveSettingsNavigationTarget } from '@/features/settings/settings-navigation';
import { GlobalCommandPaletteProvider } from '@/features/settings/components/GlobalCommandPaletteProvider';

/**
 * The epic's structural gate.
 *
 * Every tile on the Control Center promises two things: it goes somewhere, and
 * once you are there you can get back. Nothing enforced either, so a renamed
 * route left a dead tile and a page reached from the hub could be a dead end.
 *
 * Rendering all 27 destinations for real would drag in the whole data layer to
 * assert a heading count. Instead this walks the registry against the router for
 * reachability, then drives the real shell across every destination it owns with
 * embedded pages that *misbehave unless correctly embedded* — so a tab that
 * forgets to pass `embedded` fails here rather than in a screenshot.
 */

const HERE = dirname(fileURLToPath(import.meta.url));
const APP_TSX = resolve(HERE, '../../../App.tsx');

/** Route paths declared in App.tsx, resolved to absolute paths. */
function declaredRoutePaths(): Set<string> {
  const source = readFileSync(APP_TSX, 'utf8');
  const paths = new Set<string>();

  // Top-level routes: `<Route path="analytics" .../>` -> `/analytics`
  for (const m of source.matchAll(/<Route\s+path="([^"]+)"/g)) {
    const p = m[1];
    if (p === '*') continue;
    paths.add(p.startsWith('/') ? p : `/${p}`);
  }

  // The `admin` element route nests its children, so re-add them under /admin.
  const adminBlock = source.slice(
    source.indexOf('<Route path="admin"'),
    source.indexOf('<Route path="slicer"'),
  );
  if (adminBlock) {
    paths.add('/admin'); // <Route index>
    for (const m of adminBlock.matchAll(/<Route\s+path="([^"]+)"/g)) {
      if (m[1] !== 'admin') paths.add(`/admin/${m[1]}`);
    }
  }

  const locationsBlock = source.slice(
    source.indexOf('<Route path="locations"'),
    source.indexOf('<Route path="catalog"'),
  );
  if (locationsBlock) {
    paths.add('/locations');
    for (const m of locationsBlock.matchAll(/<Route\s+path="([^"]+)"/g)) {
      if (m[1] !== 'locations') paths.add(`/locations/${m[1]}`);
    }
  }

  return paths;
}

const ADMIN_OWNED = ADMIN_DESTINATIONS.filter((d) => d.path.startsWith('/admin'));

// --- shell dependencies ----------------------------------------------------
// Only the environment is faked. Every embedded page below renders its own h1
// unless the shell passes `embedded`, which is exactly the contract under test.
// Each factory builds its own component: `vi.mock` is hoisted above any shared
// module-scope helper.
//
// These stubs encode the *expected* embedded behaviour rather than importing
// the real pages, which is a deliberate tradeoff. Rendering seven real feature
// pages would drag their whole data layer into a suite that exists to measure
// the shell, and a network stub failing would look like a contract violation.
// The cost is that this suite proves the shell passes `embedded` correctly, not
// that each page honours it. That second half is covered where it belongs:
// `EmbeddedPageContract.test.ts` reads `SettingsShell.tsx` and fails if a page
// the shell embeds does not receive the prop, and `EmbeddedPageRendering.test.tsx`
// mounts the *real* pages both ways to prove they suppress their own header
// when embedded and keep it when standalone.

vi.mock('@/common/components/ThemeSwitcher', () => ({
  ThemeSwitcher: () => <div data-testid="theme-switcher">Theme Switcher</div>,
}));
vi.mock('@/features/printer-groups/pages/PrinterGroupsPage', () => ({
  PrinterGroupsPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="printer-groups-page">{!embedded && <h1>Printer Groups</h1>}<p>body</p></div>
  ),
}));
vi.mock('@/features/nfc/pages/NfcBindingsPage', () => ({
  NfcBindingsPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="nfc-bindings-page">{!embedded && <h1>NFC Bindings</h1>}<p>body</p></div>
  ),
}));
vi.mock('@/features/profile/pages/ApiKeysPage', () => ({
  ApiKeysPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="api-keys-page">{!embedded && <h1>API Keys</h1>}<p>body</p></div>
  ),
}));
vi.mock('@/features/profile/pages/PasskeysPage', () => ({
  PasskeysPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="passkeys-page">{!embedded && <h1>Passkeys</h1>}<p>body</p></div>
  ),
}));
vi.mock('@/features/notifications/pages/NotificationPreferencesPage', () => ({
  NotificationPreferencesPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="notification-preferences-page">{!embedded && <h1>Notifications</h1>}<p>body</p></div>
  ),
}));
// SettingsPage is content-only by design: it takes no `embedded` prop and
// renders no header, because it is never mounted outside a shell. Modelled
// faithfully so this walk measures the shell, not a fictional page.
vi.mock('@/features/admin/pages/SettingsPage', () => ({
  SettingsPage: () => <div data-testid="legacy-settings-page"><p>body</p></div>,
}));
vi.mock('@/features/settings/components/FarmSettingsSection', () => ({
  FarmSettingsSection: () => <div data-testid="farm-settings-section">Farm Settings</div>,
}));

// Power Monitors is a standalone admin page, not a shell tab. Only its data
// layer is faked — it renders its own real PageTemplate, which is what the
// back-link assertion measures.
vi.mock('@/features/power-monitors/hooks/usePowerMonitors', () => {
  const idle = { mutateAsync: vi.fn(), isPending: false, mutate: vi.fn() };
  return {
    usePowerMonitors: () => ({ data: [], isLoading: false, isError: false }),
    useCreatePowerMonitor: () => idle,
    useUpdatePowerMonitor: () => idle,
    useDeletePowerMonitor: () => idle,
    useTestPowerMonitorConnection: () => idle,
  };
});
vi.mock('@/common/hooks/useApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/common/hooks/useApi')>()),
  usePrintersFast: () => ({ data: [] }),
}));

vi.mock('@/features/auth/hooks/useAuth', () => {
  const user = { id: '1', email: 'admin@test.com', isActive: true, roles: ['farm_admin'] };
  const value = {
    user,
    isAuthenticated: true,
    isLoading: false,
    hasRole: () => true,
    hasPermission: () => true,
    logout: vi.fn(),
  };
  return { useAuth: () => value, useAuthInternal: () => value };
});

vi.mock('@/common/hooks/useTheme', () => ({
  useTheme: () => ({
    theme: 'dark',
    setTheme: vi.fn(),
    themes: ['light', 'dark'],
    isLight: false,
    isDark: true,
  }),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true }),
}));

vi.mock('sonner', () => ({
  toast: { info: vi.fn(), error: vi.fn(), success: vi.fn() },
}));

function renderShellAt(path: string) {
  const scope = path.startsWith('/admin/manage') ? 'admin' : 'system';
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <GlobalCommandPaletteProvider>
          <SettingsShell routeScope={scope} />
        </GlobalCommandPaletteProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

/**
 * Real renders for the admin destinations the shell does not own. Keyed by
 * path so the registry walk below can prove every such destination is covered
 * rather than silently skipped — the omission that let `/admin/power-monitors`
 * ship with no way back to the hub.
 */
const STANDALONE_RENDERERS: Record<string, () => void> = {
  '/admin/power-monitors': () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/admin/power-monitors']}>
          <PowerMonitorSettingsPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );
  },
};

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation(() => ({
      matches: false,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    })),
  });
});

beforeEach(() => {
  vi.clearAllMocks();
});

describe('admin destination contract (#1016)', () => {
  it('has destinations to check', () => {
    // Guards the walks below: an empty registry would make every `it.each` vacuous.
    expect(ADMIN_DESTINATIONS.length).toBeGreaterThan(20);
    expect(ADMIN_OWNED.length).toBeGreaterThan(15);
  });

  describe('every tile goes somewhere', () => {
    const declared = declaredRoutePaths();

    it('reads the real route table', () => {
      // If this ever comes back empty the walk below would pass by accident.
      expect(declared.has('/admin')).toBe(true);
      expect(declared.has('/admin/settings')).toBe(true);
      expect(declared.has('/admin/manage')).toBe(true);
    });

    it.each(ADMIN_DESTINATIONS.map((d) => [d.id, d.path] as const))(
      '%s -> %s is a declared route',
      (_id, path) => {
        const pathname = path.split('?')[0];
        expect(declared.has(pathname)).toBe(true);
      },
    );
  });

  describe('every deep link resolves to the tab it names', () => {
    // Checking the pathname alone is not enough. `resolveSettingsNavigationTarget`
    // silently falls back to a scope's default category when it does not recognise
    // one, so a destination pointing at `?tab=typo` still renders a perfectly good
    // page — just the wrong one. That is a false pass in the dangerous direction:
    // the tile looks fine and quietly lands somewhere else. Asserting the resolver
    // hands back the tab and sub-page the URL actually asked for is what closes it.
    const deepLinks = ADMIN_DESTINATIONS.filter((d) => d.path.includes('?')).map((d) => {
      const [pathname, query] = d.path.split('?');
      const params = new URLSearchParams(query);
      return [d.id, d.path, pathname, params.get('scope'), params.get('tab'), params.get('sub')] as const;
    });

    it('has deep links to check', () => {
      expect(deepLinks.length).toBeGreaterThan(10);
    });

    it.each(deepLinks)('%s -> %s resolves to its own tab', (_id, _path, pathname, scope, tab, sub) => {
      // The route itself fixes the scope; `?scope=` is only ever a redundant echo.
      const routeScope = scope ?? (pathname === '/admin/manage' ? 'admin' : 'system');
      const resolved = resolveSettingsNavigationTarget(tab, sub, routeScope);

      // Deliberately an identity check, not "resolves to *something*".
      // A non-canonical tab silently falls back to the route scope's default,
      // landing somewhere other than the tab named by the tile. This identity
      // assertion ensures registry paths use canonical category ids.
      expect(resolved.categoryId).toBe(tab);
      if (sub) {
        expect(resolved.subPageId).toBe(sub);
      }
    });
  });

  describe('every destination the shell owns keeps one h1 and a way back', () => {
    const shellOwned = ADMIN_OWNED.filter(
      (d) => d.path.startsWith('/admin/settings') || d.path.startsWith('/admin/manage'),
    );

    it('covers most of the admin surface', () => {
      expect(shellOwned.length).toBeGreaterThan(15);
    });

    it.each(shellOwned.map((d) => [d.id, d.path] as const))(
      '%s renders exactly one h1',
      (_id, path) => {
        renderShellAt(path);
        expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
      },
    );

    it.each(shellOwned.map((d) => [d.id, d.path] as const))(
      '%s links back to the Control Center',
      (_id, path) => {
        renderShellAt(path);
        expect(screen.getByRole('link', { name: ADMIN_HUB_PARENT.label })).toHaveAttribute(
          'href',
          ADMIN_HUB_PARENT.to,
        );
      },
    );
  });

  it('does not offer an admin back link on the personal settings page', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/settings']}>
          <GlobalCommandPaletteProvider>
            <SettingsShell routeScope="user" />
          </GlobalCommandPaletteProvider>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    // /settings was never below /admin, so a link "back" to it would be a lie.
    expect(
      screen.queryByRole('link', { name: ADMIN_HUB_PARENT.label }),
    ).not.toBeInTheDocument();
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
  });
});

/**
 * The two admin destinations the settings shell does *not* draw.
 *
 * `/admin` is the hub itself — a link back to where you already are would be
 * noise. `/admin/power-monitors` is a standalone page that renders its own
 * `PageTemplate`, so the shell's back link never applied to it. That is exactly
 * how it shipped without one: the suite above only walks shell-owned paths, so
 * the hub had a tile that led to a dead end.
 *
 * This block closes the registry. Every admin-owned destination is either drawn
 * by the shell, exempt for a stated reason, or covered here by a real render —
 * and a new one that is none of those fails the first test in the block.
 */
/**
 * Routes that exist in the router but deliberately have no hub tile, each with
 * the reason. Anything not listed here must be registered in
 * `ADMIN_DESTINATIONS` so the Control Center can actually reach it.
 */
const ROUTER_ONLY: Record<string, string> = {
  '/admin/power-monitors': 'reached from the Hardware tile, not a top-level destination',
};

describe('admin destinations the shell does not draw (Hicks #5)', () => {
  const NOT_SHELL_DRAWN = ADMIN_OWNED.filter(
    (d) => !d.path.startsWith('/admin/settings') && !d.path.startsWith('/admin/manage'),
  );

  /** Paths that legitimately have no back link, each with the reason. */
  const EXEMPT: Record<string, string> = {
    '/admin': 'the hub itself — it cannot link back to itself',
  };
  it('has an owner for every destination outside the shell', () => {
    const unaccounted = NOT_SHELL_DRAWN.map((d) => d.path).filter(
      (p) => !(p in EXEMPT) && !(p in STANDALONE_RENDERERS),
    );
    expect(unaccounted).toEqual([]);
  });

  it.each(Object.keys(STANDALONE_RENDERERS))('%s links back to the Control Center', (path) => {
    STANDALONE_RENDERERS[path]();
    expect(screen.getByRole('link', { name: ADMIN_HUB_PARENT.label })).toHaveAttribute(
      'href',
      ADMIN_HUB_PARENT.to,
    );
  });

  it.each(Object.keys(STANDALONE_RENDERERS))('%s renders exactly one h1', (path) => {
    STANDALONE_RENDERERS[path]();
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
  });

  /**
   * The reverse direction. Every assertion above starts from
   * `ADMIN_DESTINATIONS`, so a route that exists in the router but was never
   * added to the registry is invisible to all of them: no hub tile, no back-link
   * check, no h1 check. That is the same shape of gap that let
   * `/admin/power-monitors` ship without a back link, one level up.
   *
   * Walk the router instead, and require the registry to account for each route.
   */
  it('registers every /admin route declared in the router', () => {
    // Registry entries are deep links (`/admin/settings?scope=system&tab=…`),
    // so compare on the pathname or every settings destination looks unrelated
    // to the `/admin/settings` route that serves it.
    const registered = new Set(ADMIN_DESTINATIONS.map((d) => d.path.split('?')[0]));
    const routerAdminPaths = [...declaredRoutePaths()].filter(
      (p) => p.startsWith('/admin') && !p.includes(':'),
    );

    const unregistered = routerAdminPaths.filter(
      (p) => !registered.has(p) && !(p in ROUTER_ONLY),
    );
    expect(unregistered).toEqual([]);
  });
});
