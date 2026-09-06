import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { SettingsSidebar } from '@/features/settings/components/SettingsSidebar';
import { SETTINGS_SCOPES, SETTINGS_CATEGORIES } from '@/features/settings/types';

const ALL_SCOPES = SETTINGS_SCOPES;
const ALL_CATEGORIES = SETTINGS_CATEGORIES;

function renderSidebar(overrides: Partial<Parameters<typeof SettingsSidebar>[0]> = {}) {
  const onCategoryChange = vi.fn();
  const result = render(
    <SettingsSidebar
      categories={ALL_CATEGORIES}
      activeScope="user"
      activeCategory="profile"
      availableScopes={ALL_SCOPES}
      onCategoryChange={onCategoryChange}
      {...overrides}
    />,
  );
  return { ...result, onCategoryChange };
}

/** The desktop nav; the mobile selector renders a duplicate set behind `md:hidden`. */
function desktopNav() {
  return screen.getAllByRole('navigation')[0];
}

describe('SettingsSidebar is a flat grouped nav', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('offers no scope switcher and no scope pill', () => {
    renderSidebar();

    // Picking a category already resolves its scope, so a scope control is a
    // second click that decides nothing.
    expect(screen.queryByRole('radiogroup')).not.toBeInTheDocument();
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Admin' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'System Settings' })).not.toBeInTheDocument();
  });

  it('lists every reachable category at once, under its scope caption', () => {
    renderSidebar();
    const nav = within(desktopNav());

    for (const caption of ['User', 'System']) {
      expect(nav.getByRole('heading', { level: 2, name: caption })).toBeInTheDocument();
    }
    for (const category of ALL_CATEGORIES) {
      expect(nav.getByRole('button', { name: category.label })).toBeInTheDocument();
    }
  });

  it('reaches a category in a different scope with one click', () => {
    const { onCategoryChange } = renderSidebar();

    fireEvent.click(within(desktopNav()).getByRole('button', { name: 'Users' }));

    expect(onCategoryChange).toHaveBeenCalledTimes(1);
    expect(onCategoryChange).toHaveBeenCalledWith('users');
  });

  it('omits the caption when only one scope is reachable', () => {
    renderSidebar({
      categories: ALL_CATEGORIES.filter((category) => category.scopeId === 'user'),
      availableScopes: ALL_SCOPES.filter((scope) => scope.id === 'user'),
    });

    expect(screen.queryByRole('heading', { level: 2, name: 'User' })).not.toBeInTheDocument();
    expect(within(desktopNav()).getByRole('button', { name: 'Profile' })).toBeInTheDocument();
  });

  it('marks the active category with aria-current and the Control Center tile treatment', () => {
    renderSidebar({ activeScope: 'system', activeCategory: 'users' });
    const active = within(desktopNav()).getByRole('button', { name: 'Users' });

    expect(active).toHaveAttribute('aria-current', 'page');
    // Raised surface + hairline border + 6px radius — the same treatment the hub
    // uses for a selected subsystem tile, not a coloured left bar.
    expect(active.className).toContain('bg-pf-bg-2');
    expect(active.className).toContain('border-pf-border');
    expect(active.className).toContain('rounded-md');
  });

  it('keeps every item at the same radius as a card', () => {
    renderSidebar();

    for (const button of within(desktopNav()).getAllByRole('button')) {
      expect(button.className).toContain('rounded-md');
      expect(button.className).not.toMatch(/rounded-(xl|2xl|3xl|full)/);
      expect(button.className).not.toMatch(/rounded-\[/);
    }
  });

  it('ends after its items instead of stretching down the page', () => {
    // Measured before this guard: the nav rendered 296px wide and 1386px tall
    // around a 206px list, so 85% of it was an empty tinted slab running the
    // full length of the settings page. `h-full` is what did it — as a grid
    // item the nav would otherwise size to its content.
    renderSidebar();
    const nav = desktopNav();

    expect(nav.className).not.toMatch(/(^|\s)h-full(\s|$)/);
    expect(nav.className).toMatch(/(^|\s)(h-fit|self-start)(\s|$)/);
  });

  it('walks the whole list with arrow keys, across group boundaries', () => {
    renderSidebar();
    const nav = within(desktopNav());
    // Profile is the last item of the User group; General is the first of System.
    const profile = nav.getByRole('button', { name: 'Profile' });
    const general = nav.getByRole('button', { name: 'General' });

    profile.focus();
    fireEvent.keyDown(profile, { key: 'ArrowDown' });

    expect(general).toHaveFocus();
  });

  it('wraps from the last item back to the first', () => {
    renderSidebar();
    const nav = within(desktopNav());
    const last = nav.getByRole('button', { name: ALL_CATEGORIES[ALL_CATEGORIES.length - 1].label });
    const first = nav.getByRole('button', { name: ALL_CATEGORIES[0].label });

    last.focus();
    fireEvent.keyDown(last, { key: 'ArrowDown' });

    expect(first).toHaveFocus();
  });

  it('drops non-matching categories while filtering but keeps the grouping', () => {
    renderSidebar({
      isFiltering: true,
      matchingCategoryIds: ['users', 'data'],
      searchQuery: 'user',
    });
    const nav = within(desktopNav());

    expect(nav.getByRole('button', { name: 'Users' })).toBeInTheDocument();
    expect(nav.getByRole('button', { name: 'Data' })).toBeInTheDocument();
    expect(nav.queryByRole('button', { name: 'Profile' })).not.toBeInTheDocument();
    // Only the Admin group survives, so its caption is no longer needed.
    expect(nav.queryByRole('heading', { level: 2, name: 'System' })).not.toBeInTheDocument();
  });
});
