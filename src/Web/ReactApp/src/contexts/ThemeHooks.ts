import { useContext } from 'react';
import { ThemeContextType, ThemeProvider } from './ThemeContext';
import { createContext } from 'react';

// Re-export context via an import from ThemeContext; actual context symbol not exported there for refresh reasons
// We'll define a proxy hook expecting the context to be available on window during runtime.
// Simpler: move the context export pattern adjustment.

// In this revision, adjust: we rely on ThemeContext being attached to window by ThemeContext module.

// Since ThemeContext isn't exported directly, we provide hooks placeholder (refactor minimal placeholder to avoid complexity)

export function useTheme(): ThemeContextType {
  // @ts-ignore
  const ctx = (window.__PF_THEME_CTX__ as ThemeContextType | undefined);
  if (!ctx) {
    throw new Error('Theme context not initialized');
  }
  return ctx;
}

export function useComputedTheme(): 'dark' | 'light' {
  const { computedTheme } = useTheme();
  return computedTheme;
}

export function useAccessibilityPreferences() {
  const { prefersReducedMotion, prefersHighContrast } = useTheme();
  return { prefersReducedMotion, prefersHighContrast };
}
