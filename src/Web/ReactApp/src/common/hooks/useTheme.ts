/**
 * useTheme — Design System v2 hook
 *
 * Exposes only the four new canonical themes. Wraps the full ThemeContext
 * internally so existing code using ThemeContext directly is unaffected.
 */
import { useTheme as useThemeContext } from '@/contexts/ThemeContext';

export type NewTheme = 'light' | 'dark' | 'matrix' | 'blueprint';

const NEW_THEMES: NewTheme[] = ['light', 'dark', 'matrix', 'blueprint'];

function isNewTheme(t: string): t is NewTheme {
  return NEW_THEMES.includes(t as NewTheme);
}

export function useTheme() {
  const ctx = useThemeContext();

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
    prefersReducedMotion: ctx.prefersReducedMotion,
    prefersHighContrast: ctx.prefersHighContrast,
  };
}
