import { useThemeInternal } from './ThemeHooks';

export function useTheme() { return useThemeInternal(); }
export function useComputedTheme(): 'dark' | 'light' { return useThemeInternal().computedTheme; }

export function useAccessibilityPreferences() {
  const { prefersReducedMotion, prefersHighContrast } = useThemeInternal();
  return { prefersReducedMotion, prefersHighContrast };
}
