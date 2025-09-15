/* eslint-disable react-refresh/only-export-components */
import React, { createContext, useEffect, useState, ReactNode, useContext } from 'react';

export type ThemeName = 'dark' | 'light' | 'system';

export interface ThemeContextType {
  /** Current theme setting ('dark', 'light', or 'system') */
  theme: ThemeName;
  /** Computed theme after system preference resolution */
  computedTheme: 'dark' | 'light';
  /** Set the theme preference */
  setTheme: (theme: ThemeName) => void;
  /** Toggle between dark and light themes */
  toggleTheme: () => void;
  /** Check if the user prefers reduced motion */
  prefersReducedMotion: boolean;
  /** Check if the user prefers high contrast */
  prefersHighContrast: boolean;
}

export const ThemeContext = createContext<ThemeContextType | undefined>(undefined);

interface ThemeProviderProps {
  children: ReactNode;
  /** Optional default theme (defaults to 'system') */
  defaultTheme?: ThemeName;
  /** Storage key for theme persistence (defaults to 'pf-theme') */
  storageKey?: string;
}

/**
 * Theme provider that manages PrintFarmer's theme state and system preferences
 */
export function ThemeProvider({ 
  children, 
  defaultTheme = 'dark',
  storageKey = 'pf-theme'
}: ThemeProviderProps) {
  const [theme, setThemeState] = useState<ThemeName>(defaultTheme);
  const [systemTheme, setSystemTheme] = useState<'dark' | 'light'>('dark');
  const [prefersReducedMotion, setPrefersReducedMotion] = useState(false);
  const [prefersHighContrast, setPrefersHighContrast] = useState(false);

  // Initialize theme from localStorage on mount
  useEffect(() => {
    const storedTheme = localStorage.getItem(storageKey) as ThemeName;
    if (storedTheme && ['dark', 'light', 'system'].includes(storedTheme)) {
      setThemeState(storedTheme);
    }
  }, [storageKey]);

  // Listen for system theme changes
  useEffect(() => {
    const mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
    
    const handleChange = (e: MediaQueryListEvent) => {
      setSystemTheme(e.matches ? 'dark' : 'light');
    };
    
    // Set initial value
    setSystemTheme(mediaQuery.matches ? 'dark' : 'light');
    
    // Listen for changes
    mediaQuery.addEventListener('change', handleChange);
    
    return () => mediaQuery.removeEventListener('change', handleChange);
  }, []);

  // Listen for accessibility preferences
  useEffect(() => {
    // Reduced motion preference
    const reducedMotionQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
    const handleReducedMotionChange = (e: MediaQueryListEvent) => {
      setPrefersReducedMotion(e.matches);
    };
    setPrefersReducedMotion(reducedMotionQuery.matches);
    reducedMotionQuery.addEventListener('change', handleReducedMotionChange);

    // High contrast preference
    const highContrastQuery = window.matchMedia('(prefers-contrast: high)');
    const handleHighContrastChange = (e: MediaQueryListEvent) => {
      setPrefersHighContrast(e.matches);
    };
    setPrefersHighContrast(highContrastQuery.matches);
    highContrastQuery.addEventListener('change', handleHighContrastChange);

    return () => {
      reducedMotionQuery.removeEventListener('change', handleReducedMotionChange);
      highContrastQuery.removeEventListener('change', handleHighContrastChange);
    };
  }, []);

  // Apply theme to document (also manage .dark class for Tailwind dark mode)
  useEffect(() => {
    const root = window.document.documentElement;
    
    // Remove previous theme classes
    root.removeAttribute('data-theme');
    root.classList.remove('dark');
    
    // Compute the actual theme to apply
    const computedTheme = theme === 'system' ? systemTheme : theme;
    
    // Apply theme
    if (computedTheme === 'light') {
      root.setAttribute('data-theme', 'light');
    } else {
      root.classList.add('dark');
    }
    
    // Apply accessibility preferences
    if (prefersReducedMotion) {
      root.style.setProperty('--pf-transition-duration', '0ms');
    } else {
      root.style.removeProperty('--pf-transition-duration');
    }
    // Broadcast change
    window.dispatchEvent(new CustomEvent('themeChange', { 
      detail: { theme, computedTheme }
    }));
  }, [theme, systemTheme, prefersReducedMotion, prefersHighContrast]);

  const setTheme = (newTheme: ThemeName) => {
    setThemeState(newTheme);
    localStorage.setItem(storageKey, newTheme);
  };

  const toggleTheme = () => {
    if (theme === 'system') {
      // If currently system, toggle to opposite of system preference
      setTheme(systemTheme === 'dark' ? 'light' : 'dark');
    } else {
      // Toggle between dark and light
      setTheme(theme === 'dark' ? 'light' : 'dark');
    }
  };

  const computedTheme = theme === 'system' ? systemTheme : theme;

  const value: ThemeContextType = {
    theme,
    computedTheme,
    setTheme,
    toggleTheme,
    prefersReducedMotion,
    prefersHighContrast,
  };

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

/**
 * Hook to use the theme context
 * @throws Error if used outside of ThemeProvider
 */
// Hooks moved to ThemeHooks.ts
export function useThemeInternal(): ThemeContextType {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error('useTheme must be used within a ThemeProvider');
  return ctx;
}

// Public hook exports (to support direct imports from ThemeContext as guided by lint rules)
export function useTheme() { return useThemeInternal(); }
export function useComputedTheme(): 'dark' | 'light' { return useThemeInternal().computedTheme; }
export function useAccessibilityPreferences() {
  const { prefersReducedMotion, prefersHighContrast } = useThemeInternal();
  return { prefersReducedMotion, prefersHighContrast };
}
export function useThemeToggle() {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error('useThemeToggle must be used within a ThemeProvider');

  const isLight = ctx.theme === 'light';
  const isDark = ctx.theme === 'dark';
  const isSystem = ctx.theme === 'system';

  return {
    theme: ctx.theme,
    computedTheme: ctx.computedTheme,
    isLight,
    isDark,
    isSystem,
    toggleTheme: ctx.toggleTheme,
    setTheme: ctx.setTheme,
    prefersReducedMotion: ctx.prefersReducedMotion,
    prefersHighContrast: ctx.prefersHighContrast,
  };
}