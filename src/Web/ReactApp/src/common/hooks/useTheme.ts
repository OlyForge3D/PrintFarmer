/**
 * useTheme - Design System v2 hook
 *
 * Exposes the canonical design-system themes. Wraps the full ThemeContext
 * internally so existing code using ThemeContext directly is unaffected.
 */
import { useTheme as useThemeContext } from '@/contexts/ThemeContext';
// Imported from the registry, not ThemeContext: a bare vi.mock() of the context
// would auto-mock this constant away and break module evaluation.
import { SELECTABLE_THEMES, isSelectableTheme } from '@/design-system/themes/registry';
import type { SelectableTheme } from '@/design-system/themes/registry';

export type NewTheme = SelectableTheme;

const NEW_THEMES: readonly NewTheme[] = SELECTABLE_THEMES;

export function useTheme() {
  const ctx = useThemeContext();

  // `computedTheme` already excludes 'system', and retired names are migrated
  // on read in ThemeContext, so this guard is belt-and-braces rather than a
  // silent coercion of a theme the user actually chose.
  const activeTheme: NewTheme = isSelectableTheme(ctx.computedTheme) ? ctx.computedTheme : 'dark';

  const setTheme = (theme: NewTheme) => ctx.setTheme(theme);

  return {
    theme: activeTheme,
    setTheme,
    themes: NEW_THEMES,
    isLight: activeTheme === 'light',
    isDark: activeTheme === 'dark',
    isMatrix: activeTheme === 'matrix',
    isBlueprint: activeTheme === 'blueprint',
    isRatos: activeTheme === 'ratos',
    isVoron: activeTheme === 'voron',
    isFarm: activeTheme === 'farm',
    prefersReducedMotion: ctx.prefersReducedMotion,
    prefersHighContrast: ctx.prefersHighContrast,
  };
}