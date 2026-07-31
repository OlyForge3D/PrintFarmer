import { beforeAll, beforeEach, describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SettingsShell } from '@/features/settings/pages/SettingsShell';
import { GlobalCommandPaletteProvider } from '@/features/settings/components/GlobalCommandPaletteProvider';

/**
 * Accessibility floor for the admin surface.
 *
 * The issue asked to "run the existing a11y helper". There isn't one: the repo
 * has no axe/jest-axe dependency and no shared helper. Adding axe here would
 * pull in a new testing tool against repo convention *and* surface a backlog of
 * pre-existing violations across unrelated components, which is a separate piece
 * of work from this epic.
 *
 * So this asserts directly the structural properties this epic could plausibly
 * have broken — heading hierarchy, landmarks, and accessible names. Those are
 * the checks that actually bear on a layout-and-chrome change; colour contrast
 * and ARIA semantics of individual widgets are unchanged by it.
 */

vi.mock('@/common/components/ThemeSwitcher', () => ({
  ThemeSwitcher: () => <div data-testid="theme-switcher">Theme Switcher</div>,
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
vi.mock('@/hooks/useSlicer', () => ({ useSlicer: () => ({ isSlicerAvailable: true }) }));
vi.mock('sonner', () => ({ toast: { info: vi.fn(), error: vi.fn(), success: vi.fn() } }));
vi.mock('@/features/admin/pages/SettingsPage', () => ({
  SettingsPage: () => <div data-testid="legacy-settings-page">settings body</div>,
}));

const ROUTES: ReadonlyArray<readonly [string, string, 'system' | 'admin']> = [
  ['system settings', '/admin/settings?tab=general', 'system'],
  ['admin manage — users', '/admin/manage?tab=users&sub=accounts', 'admin'],
  ['admin manage — operations', '/admin/manage?tab=operations&sub=status', 'admin'],
];

function renderAt(path: string, scope: 'system' | 'admin') {
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

beforeEach(() => vi.clearAllMocks());

describe('admin surface accessibility floor (#1016)', () => {
  it.each(ROUTES)('%s has exactly one level-1 heading', (_name, path, scope) => {
    renderAt(path, scope);
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
  });

  it.each(ROUTES)('%s never skips a heading level', (_name, path, scope) => {
    const { container } = renderAt(path, scope);

    const levels = [...container.querySelectorAll('h1,h2,h3,h4,h5,h6')].map((h) =>
      Number(h.tagName[1]),
    );

    expect(levels.length).toBeGreaterThan(0);
    expect(levels[0]).toBe(1);

    // A jump from h2 straight to h4 leaves a screen-reader user guessing what
    // the missing level was.
    const jumps: string[] = [];
    for (let i = 1; i < levels.length; i++) {
      if (levels[i] > levels[i - 1] + 1) jumps.push(`h${levels[i - 1]} -> h${levels[i]}`);
    }
    expect(jumps).toEqual([]);
  });

  it.each(ROUTES)('%s names every link', (_name, path, scope) => {
    renderAt(path, scope);
    const links = screen.queryAllByRole('link');
    expect(links.length).toBeGreaterThan(0); // else this assertion means nothing
    const unnamed = links.filter(
      (el) => !(el.textContent?.trim() || el.getAttribute('aria-label')),
    );
    expect(unnamed.map((el) => el.outerHTML.slice(0, 80))).toEqual([]);
  });

  it.each(ROUTES)('%s names every button, including icon-only ones', (_name, path, scope) => {
    renderAt(path, scope);
    const buttons = screen.queryAllByRole('button');
    expect(buttons.length).toBeGreaterThan(0);
    const unnamed = buttons.filter(
        (el) =>
          !(
            el.textContent?.trim() ||
            el.getAttribute('aria-label') ||
            el.getAttribute('aria-labelledby') ||
            el.getAttribute('title')
          ),
      );
    expect(unnamed.map((el) => el.outerHTML.slice(0, 80))).toEqual([]);
  });

  it.each(ROUTES)('%s exposes the settings nav as a named landmark', (_name, path, scope) => {
    const { container } = renderAt(path, scope);
    const navs = [...container.querySelectorAll('nav')];
    expect(navs.length).toBeGreaterThan(0);

    // An unnamed <nav> is indistinguishable from any other in a landmark list.
    const unnamed = navs.filter(
      (n) => !(n.getAttribute('aria-label') || n.getAttribute('aria-labelledby')),
    );
    expect(unnamed.map((n) => n.outerHTML.slice(0, 80))).toEqual([]);
  });

  it.each(ROUTES)('%s marks the active nav item for assistive tech', (_name, path, scope) => {
    const { container } = renderAt(path, scope);
    const nav = container.querySelector('nav');
    expect(nav).not.toBeNull();

    // Highlighting the current page with colour alone does not reach a
    // screen reader; aria-current does.
    const current = within(nav as HTMLElement)
      .queryAllByRole('button')
      .concat(within(nav as HTMLElement).queryAllByRole('link'))
      .filter((el) => el.getAttribute('aria-current') === 'page');

    expect(current.length).toBeGreaterThan(0);
  });
});
