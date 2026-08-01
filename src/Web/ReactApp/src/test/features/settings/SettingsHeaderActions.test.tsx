import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SettingsShell } from '@/features/settings/pages/SettingsShell';
import { GlobalCommandPaletteProvider } from '@/features/settings/components/GlobalCommandPaletteProvider';
import { SettingsHeaderPortal } from '@/features/settings/components/SettingsHeaderPortal';
import { SettingsHeaderSlotContext } from '@/features/settings/components/settingsHeaderSlotContext';

/**
 * #1010 — the settings shell's page-level controls live in the page header's
 * `actions` slot, the same place `AdminControlCenterPage` puts its own.
 *
 * The two things worth pinning down are structural, not cosmetic:
 *
 * 1. The controls are genuinely *inside* the page header, not merely somewhere
 *    on the page. A test that only asserted "the button exists" would still
 *    pass if the old floating toolbar came back.
 * 2. A page's own header control reaches the slot by portal, and degrades to
 *    rendering in place when there is no slot — so mounting a settings page
 *    outside the shell never silently deletes a working control.
 */

vi.mock('@/common/components/ThemeSwitcher', () => ({
  ThemeSwitcher: () => <div data-testid="theme-switcher">Theme Switcher</div>,
}));

vi.mock('@/features/admin/pages/SettingsPage', () => ({
  SettingsPage: () => <div data-testid="legacy-settings-page">Legacy Settings Page</div>,
}));

vi.mock('@/features/settings/components/FarmSettingsSection', () => ({
  FarmSettingsSection: () => <div data-testid="farm-settings-section">Farm Settings Section</div>,
}));

vi.mock('@/features/auth/hooks/useAuth', () => {
  const user = {
    id: '1',
    email: 'admin@test.com',
    isActive: true,
    roles: ['farm_admin'],
  };
  const value = {
    user,
    isAuthenticated: true,
    isLoading: false,
    hasRole: (role: string) => user.roles.includes(role),
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

function renderShell(initialRoute = '/settings') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialRoute]}>
        <GlobalCommandPaletteProvider>
          <SettingsShell />
        </GlobalCommandPaletteProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

/** The header block PageTemplate renders: the nearest ancestor containing the h1. */
function pageHeaderOf(element: HTMLElement): HTMLElement {
  const heading = screen.getAllByRole('heading', { level: 1 })[0];
  let node: HTMLElement | null = element;
  while (node) {
    if (node.contains(heading)) {
      return node;
    }
    node = node.parentElement;
  }
  throw new Error('element is not inside the page header');
}

describe('settings shell header actions (#1010)', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('puts the palette trigger in the page header, alongside the title', () => {
    renderShell();

    const launcher = screen.getByRole('button', { name: /search settings/i });

    // Not just "on the page" — in the same header block as the h1. This is what
    // distinguishes the actions slot from the floating toolbar it replaced.
    expect(pageHeaderOf(launcher)).toBeTruthy();
  });

  it('shows the shortcut on the trigger so the keystroke is discoverable', () => {
    renderShell();

    const launcher = screen.getByRole('button', { name: /search settings/i });
    expect(launcher.textContent).toMatch(/(⌘K|Ctrl K)/);
  });

  it('has no sticky toolbar left anywhere in the shell', () => {
    const { container } = renderShell();

    // The old chrome was a `sticky top-0 z-20` bar wrapping the palette button.
    expect(container.querySelector('.sticky.top-0.z-20')).toBeNull();
  });

  it('reserves header room from a measured variable, not a hardcoded margin', () => {
    renderShell();

    const heading = screen.getAllByRole('heading', { level: 1 })[0];
    const header = heading.closest('[class*="lg:mr-"]');

    expect(header).not.toBeNull();
    expect(header?.className).toContain('lg:mr-[var(--pf-floating-bar-inset,0px)]');
    expect(header?.className).not.toContain('lg:mr-72');
  });

  it('lets the actions row wrap rather than overflow on narrow viewports', () => {
    renderShell();

    const launcher = screen.getByRole('button', { name: /search settings/i });
    const actionsRow = launcher.closest('.flex-wrap');

    expect(actionsRow).not.toBeNull();
  });
});

describe('SettingsHeaderPortal (#1010)', () => {
  it('renders its children into the slot when the shell provides one', () => {
    const slot = document.createElement('div');
    slot.setAttribute('data-testid', 'slot');
    document.body.append(slot);

    render(
      <SettingsHeaderSlotContext.Provider value={slot}>
        <div data-testid="content">
          <SettingsHeaderPortal>
            <button type="button">Toggle</button>
          </SettingsHeaderPortal>
        </div>
      </SettingsHeaderSlotContext.Provider>,
    );

    const toggle = screen.getByRole('button', { name: 'Toggle' });
    expect(slot).toContainElement(toggle);
    expect(screen.getByTestId('content')).not.toContainElement(toggle);

    slot.remove();
  });

  it('renders in place when there is no slot, so the control is never lost', () => {
    render(
      <div data-testid="content">
        <SettingsHeaderPortal>
          <button type="button">Toggle</button>
        </SettingsHeaderPortal>
      </div>,
    );

    const toggle = screen.getByRole('button', { name: 'Toggle' });
    expect(screen.getByTestId('content')).toContainElement(toggle);
  });
});
