/* eslint-disable react-refresh/only-export-components */
import React, { createContext, useContext, useEffect, useState, ReactNode, useCallback } from 'react';

/**
 * The themes a user can actually pick. Each has a stylesheet in
 * `src/design-system/themes/` and an entry in `ThemeSwitcher`.
 *
 * Keep in sync with:
 *   - the `VALID` list in `index.html` (omitting one there causes a flash of
 *     `dark` on load until React hydrates)
 *   - `THEME_OPTIONS` in `src/common/components/ThemeSwitcher.tsx`
 * `src/test/contexts/themeRegistry.test.ts` fails if these drift apart.
 */
export const SELECTABLE_THEMES = ['light', 'dark', 'matrix', 'blueprint', 'ratos', 'voron', 'farm'] as const;

export type SelectableTheme = (typeof SELECTABLE_THEMES)[number];

export type Theme = SelectableTheme | 'system';

interface ThemeContextType {
  theme: Theme;
  computedTheme: Exclude<Theme, 'system'>;
  setTheme: (theme: Theme) => void;
  toggleTheme: () => void;
  isLight: boolean;
  isDark: boolean;
  isSystem: boolean;
  prefersReducedMotion: boolean;
  prefersHighContrast: boolean;
}

const ThemeContext = createContext<ThemeContextType | undefined>(undefined);

const THEME_STORAGE_KEY = 'pf-theme';
const LEGACY_STORAGE_KEY = 'printfarmer-theme';

/**
 * Themes that were removed. Their stylesheets lived in `src/styles/themes/`,
 * which is imported into `layer(base)` while the design-system themes are
 * unlayered — so their declarations never won the cascade and every one of
 * them rendered byte-identical `dark`. Migrating them to `dark` preserves what
 * users were actually seeing.
 */
const RETIRED_THEME_MAP: Record<string, Theme> = {
  'printfarmer-dark': 'dark',
  'github-dark': 'dark',
  forge: 'dark',
};

function normalizeTheme(value: string | null, fallback: Theme): Theme {
  if (!value) return fallback;
  const retired = RETIRED_THEME_MAP[value];
  if (retired) return retired;
  if (value === 'system' || (SELECTABLE_THEMES as readonly string[]).includes(value)) {
    return value as Theme;
  }
  return fallback;
}

export function ThemeProvider({ 
  children, 
  defaultTheme = 'dark' as Theme,
  storageKey = THEME_STORAGE_KEY 
}: { 
  children: ReactNode;
  defaultTheme?: Theme;
  storageKey?: string;
}) {
  const [theme, setThemeState] = useState<Theme>(() => {
    // Only migrate from old storage key when using the default key (not custom test keys)
    if (storageKey === THEME_STORAGE_KEY) {
      const legacy = localStorage.getItem(LEGACY_STORAGE_KEY);
      if (legacy) {
        const mapped = normalizeTheme(legacy, defaultTheme);
        localStorage.setItem(storageKey, mapped);
        localStorage.removeItem(LEGACY_STORAGE_KEY);
        return mapped;
      }
    }
    return normalizeTheme(localStorage.getItem(storageKey), defaultTheme);
  });

  // Track system preference for 'system' theme
  const [systemPrefersDark, setSystemPrefersDark] = useState(() => 
    typeof window !== 'undefined' 
      ? window.matchMedia('(prefers-color-scheme: dark)').matches 
      : false
  );

  // Derive computed theme from theme setting and system preference
  const computedTheme: Exclude<Theme, 'system'> = theme === 'system' 
    ? (systemPrefersDark ? 'dark' : 'light')
    : (theme as Exclude<Theme, 'system'>);

  const [accessibility, setAccessibility] = useState(() => {
    // Initialize accessibility preferences from matchMedia on first render
    if (typeof window === 'undefined') {
      return { prefersReducedMotion: false, prefersHighContrast: false };
    }
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const highContrast = window.matchMedia('(prefers-contrast: more)');
    return {
      prefersReducedMotion: reducedMotion?.matches ?? false,
      prefersHighContrast: highContrast?.matches ?? false,
    };
  });

  // Subscribe to system preference changes
  useEffect(() => {
    const darkModePreference = window.matchMedia('(prefers-color-scheme: dark)');
    
    const handleChange = (e: MediaQueryListEvent) => {
      setSystemPrefersDark(e.matches);
    };

    darkModePreference.addEventListener('change', handleChange);
    return () => darkModePreference.removeEventListener('change', handleChange);
  }, []);

  // Subscribe to accessibility preference changes
  useEffect(() => {
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const highContrast = window.matchMedia('(prefers-contrast: more)');

    // Only proceed if matchMedia returned valid objects
    if (!reducedMotion || !highContrast) return;

    const handleReducedMotionChange = (e: MediaQueryListEvent) => {
      setAccessibility(prev => ({ ...prev, prefersReducedMotion: e.matches }));
    };
    const handleHighContrastChange = (e: MediaQueryListEvent) => {
      setAccessibility(prev => ({ ...prev, prefersHighContrast: e.matches }));
    };

    reducedMotion.addEventListener('change', handleReducedMotionChange);
    highContrast.addEventListener('change', handleHighContrastChange);

    return () => {
      reducedMotion.removeEventListener('change', handleReducedMotionChange);
      highContrast.removeEventListener('change', handleHighContrastChange);
    };
  }, []);

  // Apply theme to DOM and save to localStorage
  useEffect(() => {
    // Every theme is explicit. There is no "default with no attribute" case —
    // index.html sets data-theme before first paint, so a bare :root selector
    // would never match anyway.
    document.documentElement.setAttribute('data-theme', computedTheme);

    // Apply CSS variables for accessibility
    if (accessibility.prefersReducedMotion) {
      document.documentElement.style.setProperty('--motion-safe', '0');
    } else {
      document.documentElement.style.removeProperty('--motion-safe');
    }

    // Save theme choice to localStorage
    localStorage.setItem(storageKey, theme);

    // Dispatch custom event for theme change
    window.dispatchEvent(new CustomEvent('themeChange', {
      detail: { theme, computedTheme, ...accessibility }
    }));
  }, [theme, computedTheme, accessibility, storageKey]);

  const setTheme = useCallback((newTheme: Theme) => {
    setThemeState(newTheme);
  }, []);

  const toggleTheme = useCallback(() => {
    setThemeState(current => {
      const cycle: Theme[] = [...SELECTABLE_THEMES, 'system'];
      const currentIndex = cycle.indexOf(current);
      const nextIndex = (currentIndex + 1) % cycle.length;
      return cycle[nextIndex];
    });
  }, []);

  const value: ThemeContextType = {
    theme,
    computedTheme,
    setTheme,
    toggleTheme,
    isLight: computedTheme === 'light',
    isDark: computedTheme !== 'light',
    isSystem: theme === 'system',
    prefersReducedMotion: accessibility.prefersReducedMotion,
    prefersHighContrast: accessibility.prefersHighContrast,
  };

  return (
    <ThemeContext.Provider value={value}>
      {children}
    </ThemeContext.Provider>
  );
}

export function useTheme() {
  const context = useContext(ThemeContext);
  if (context === undefined) {
    throw new Error('useTheme must be used within a ThemeProvider');
  }
  return context;
}

export function useThemeToggle() {
  const context = useContext(ThemeContext);
  if (context === undefined) {
    throw new Error('useThemeToggle must be used within a ThemeProvider');
  }
  const { theme, computedTheme, setTheme, toggleTheme, isLight, isDark, isSystem } = context;
  return {
    theme,
    computedTheme,
    setTheme,
    toggleTheme,
    isLight,
    isDark,
    isSystem,
  };
}

export function useComputedTheme() {
  const { computedTheme } = useTheme();
  return computedTheme;
}

export function useAccessibilityPreferences() {
  const { prefersReducedMotion, prefersHighContrast } = useTheme();
  return { prefersReducedMotion, prefersHighContrast };
}
