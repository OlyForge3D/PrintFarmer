/* eslint-disable react-refresh/only-export-components */
import React, { createContext, useContext, useEffect, useState, ReactNode } from 'react';

export type Theme = 'github-dark' | 'printfarmer-dark' | 'light';

interface ThemeContextType {
  theme: Theme;
  setTheme: (theme: Theme) => void;
}

const ThemeContext = createContext<ThemeContextType | undefined>(undefined);

const THEME_STORAGE_KEY = 'printfarmer-theme';

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(() => {
    // Load theme from localStorage or default to github-dark
    const stored = localStorage.getItem(THEME_STORAGE_KEY);
    return (stored as Theme) || 'github-dark';
  });

  useEffect(() => {
    // Apply theme to document
    document.documentElement.setAttribute('data-theme', theme);
    
    // Save to localStorage
    localStorage.setItem(THEME_STORAGE_KEY, theme);
  }, [theme]);

  const setTheme = (newTheme: Theme) => {
    setThemeState(newTheme);
  };

  return (
    <ThemeContext.Provider value={{ theme, setTheme }}>
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
