/**
 * The single source of truth for which themes exist.
 *
 * This lives outside `ThemeContext` deliberately. It is a plain constant with
 * no React dependency, and `ThemeContext` is one of the most frequently mocked
 * modules in the test suite — a bare `vi.mock('@/contexts/ThemeContext')`
 * auto-mocks every export, which would leave anything importing the list with
 * `undefined` at module-evaluation time.
 *
 * Every theme here must have:
 *   - a stylesheet at `src/design-system/themes/<id>.css`
 *   - an `@import` for it in `src/index.css`
 *   - an entry in `THEME_OPTIONS` in `src/common/components/ThemeSwitcher.tsx`
 *   - an entry in the `VALID` array in `index.html` (otherwise the theme
 *     flashes as `dark` on load until React hydrates)
 *
 * `src/test/contexts/themeRegistry.test.ts` fails if any of those drift apart,
 * or if two themes resolve to the same core palette.
 */
export const SELECTABLE_THEMES = [
  'light',
  'dark',
  'matrix',
  'blueprint',
  'ratos',
  'voron',
  'farm',
  'forge',
] as const;

export type SelectableTheme = (typeof SELECTABLE_THEMES)[number];

/** A user's stored preference, which may defer to the OS. */
export type Theme = SelectableTheme | 'system';

/**
 * Themes that were removed. Their stylesheets lived in `src/styles/themes/`,
 * which `index.css` imports into `layer(base)` while the design-system themes
 * are imported unlayered — so their colour tokens never won the cascade and
 * both rendered the `dark` palette.
 *
 * They were not quite byte-identical to `dark`: each design-system theme also
 * carries an `html[data-theme="<id>"] body` display font, and no such rule
 * matched these names, so they fell back to the base face. Mapping them to
 * `dark` therefore restores the display font those users should have had.
 *
 * `forge` is deliberately NOT here. Its non-token rules — a copper glow on
 * headings and progress bars — faced no competing declaration, and an
 * unopposed layered rule applies normally, so forge really did render
 * differently. It has been migrated to `src/design-system/themes/forge.css`.
 */
export const RETIRED_THEME_MAP: Record<string, Theme> = {
  'printfarmer-dark': 'dark',
  'github-dark': 'dark',
};

export function isSelectableTheme(value: string): value is SelectableTheme {
  return (SELECTABLE_THEMES as readonly string[]).includes(value);
}

/**
 * Resolves a stored or user-supplied value to a theme we can actually render,
 * migrating retired names and rejecting anything unrecognised.
 */
export function normalizeTheme(value: string | null | undefined, fallback: Theme): Theme {
  if (!value) return fallback;
  const retired = RETIRED_THEME_MAP[value];
  if (retired) return retired;
  if (value === 'system' || isSelectableTheme(value)) return value;
  return fallback;
}
