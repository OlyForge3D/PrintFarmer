// Hooks exported from a dedicated file to keep ThemeContext focused on components.
import { useContext } from 'react';
import { ThemeContext, ThemeContextType } from './ThemeContext';

export function useThemeInternal(): ThemeContextType {
	const ctx = useContext(ThemeContext);
	if (!ctx) throw new Error('useTheme must be used within a ThemeProvider');
	return ctx;
}

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
