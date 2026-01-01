/* eslint-disable react-refresh/only-export-components */
import React, { createContext, useContext, useEffect, useState, ReactNode, useCallback } from 'react';

export type Theme = 'github-dark' | 'printfarmer-dark' | 'light' | 'system';

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

const THEME_STORAGE_KEY = 'printfarmer-theme';

export function ThemeProvider({ 
  children, 
  defaultTheme = 'github-dark' as Theme,
  storageKey = THEME_STORAGE_KEY 
}: { 
  children: ReactNode;
  defaultTheme?: Theme;
  storageKey?: string;
}) {
  const [theme, setThemeState] = useState<Theme>(() => {
    // Load theme from localStorage or use default
    const stored = localStorage.getItem(storageKey);
    return (stored as Theme) || defaultTheme;
  });

  const [computedTheme, setComputedTheme] = useState<Exclude<Theme, 'system'>>('github-dark');
  const [accessibility, setAccessibility] = useState({
    prefersReducedMotion: false,
    prefersHighContrast: false,
  });

  // Compute the actual theme to apply based on system preference if needed
  useEffect(() => {
    if (theme === 'system') {
      // Check system preference for dark mode
      const darkModePreference = window.matchMedia('(prefers-color-scheme: dark)');
      setComputedTheme(darkModePreference.matches ? 'github-dark' : 'light');

      // Listen for changes to system preference
      const handleChange = (e: MediaQueryListEvent) => {
        setComputedTheme(e.matches ? 'github-dark' : 'light');
      };

      darkModePreference.addEventListener('change', handleChange);
      return () => darkModePreference.removeEventListener('change', handleChange);
    } else {
      setComputedTheme(theme as Exclude<Theme, 'system'>);
    }
  }, [theme]);

  // Check accessibility preferences
  useEffect(() => {
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const highContrast = window.matchMedia('(prefers-contrast: more)');

    const updateAccessibility = () => {
      setAccessibility({
        prefersReducedMotion: reducedMotion.matches,
        prefersHighContrast: highContrast.matches,
      });
    };

    updateAccessibility();

    reducedMotion.addEventListener('change', updateAccessibility);
    highContrast.addEventListener('change', updateAccessibility);

    return () => {
      reducedMotion.removeEventListener('change', updateAccessibility);
      highContrast.removeEventListener('change', updateAccessibility);
    };
  }, []);

  // Apply theme to DOM and save to localStorage
  useEffect(() => {
    if (computedTheme === 'light') {
      document.documentElement.setAttribute('data-theme', 'light');
    } else {
      document.documentElement.removeAttribute('data-theme');
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
      const cycle: Theme[] = ['light', 'github-dark', 'printfarmer-dark', 'system'];
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
    isDark: computedTheme === 'github-dark' || computedTheme === 'printfarmer-dark',
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
