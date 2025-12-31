import React from 'react';
import { useTheme, Theme } from '@/contexts/ThemeContext';

export function ThemeSelector() {
  const { theme, setTheme } = useTheme();

  const themes: { value: Theme; label: string; description: string }[] = [
    {
      value: 'github-dark',
      label: 'GitHub Dark',
      description: 'Dark theme inspired by GitHub and VS Code'
    },
    {
      value: 'printfarmer-dark',
      label: 'PrintFarmer Dark',
      description: 'Original PrintFarmer dark theme'
    },
    {
      value: 'light',
      label: 'Light',
      description: 'Light theme for bright environments'
    }
  ];

  return (
    <div className="space-y-3">
      <label className="text-sm font-medium text-pf-text-primary">
        Theme
      </label>
      <div className="space-y-2">
        {themes.map((t) => (
          <label
            key={t.value}
            className="flex items-start gap-3 p-3 border border-pf-border rounded-lg cursor-pointer hover:bg-pf-bg-2 transition-colors"
          >
            <input
              type="radio"
              name="theme"
              value={t.value}
              checked={theme === t.value}
              onChange={(e) => setTheme(e.target.value as Theme)}
              className="mt-1"
            />
            <div className="flex-1">
              <div className="text-sm font-medium text-pf-text-primary">
                {t.label}
              </div>
              <div className="text-xs text-pf-text-secondary">
                {t.description}
              </div>
            </div>
          </label>
        ))}
      </div>
    </div>
  );
}
