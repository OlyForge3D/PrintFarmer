/* eslint-disable react-refresh/only-export-components */
import React, { createContext, useContext, useEffect, useState, ReactNode, useCallback } from 'react';

export type Theme = 'github-dark' | 'printfarmer-dark' | 'light' | 'matrix' | 'forge' | 'dark' | 'blueprint' | 'system';

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
const LEGACY_THEME_MAP: Partial<Record<string, Theme>> = {
  'printfarmer-dark': 'dark',
  'github-dark': 'dark',
};

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
        const mapped = LEGACY_THEME_MAP[legacy] ?? (legacy as Theme);
        localStorage.setItem(storageKey, mapped);
        localStorage.removeItem(LEGACY_STORAGE_KEY);
        return mapped;
      }
    }
    const stored = localStorage.getItem(storageKey);
    return (stored as Theme) || defaultTheme;
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
    // Apply the computed theme via data-theme attribute
    // github-dark is the default (no attribute needed), others need explicit attribute
    if (computedTheme === 'github-dark') {
      document.documentElement.removeAttribute('data-theme');
    } else {
      document.documentElement.setAttribute('data-theme', computedTheme);
    }

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
    const cycle: Theme[] = ['light', 'github-dark', 'printfarmer-dark', 'matrix', 'forge', 'system'];
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
