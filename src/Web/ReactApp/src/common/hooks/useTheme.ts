/**
 * useTheme - Design System v2 hook
 *
 * Exposes the canonical design-system themes. Wraps the full ThemeContext
 * internally so existing code using ThemeContext directly is unaffected.
 */
import { useTheme as useThemeContext, SELECTABLE_THEMES } from '@/contexts/ThemeContext';
import type { SelectableTheme } from '@/contexts/ThemeContext';

export type NewTheme = SelectableTheme;

const NEW_THEMES: readonly NewTheme[] = SELECTABLE_THEMES;

function isNewTheme(t: string): t is NewTheme {
  return (SELECTABLE_THEMES as readonly string[]).includes(t);
}

export function useTheme() {
  const ctx = useThemeContext();

  // `computedTheme` already excludes 'system', and retired names are migrated
  // on read in ThemeContext, so this guard is belt-and-braces rather than a
  // silent coercion of a theme the user actually chose.
  const activeTheme: NewTheme = isNewTheme(ctx.computedTheme) ? ctx.computedTheme : 'dark';

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