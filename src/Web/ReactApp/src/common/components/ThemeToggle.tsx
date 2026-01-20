/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import { SunIcon, MoonIcon, MonitorIcon } from '@/common/components/icons/MdiIcons';
import { useTheme } from '@/contexts/ThemeContext';
import type { Theme } from '@/contexts/ThemeContext';

interface ThemeToggleProps {
  showLabels?: boolean;
  size?: 'sm' | 'md' | 'lg';
  variant?: 'buttons' | 'dropdown' | 'compact';
  className?: string;
}

/** Accessible theme toggle with three variants */
export function ThemeToggle({
  showLabels = false,
  size = 'md',
  variant = 'compact',
  className
}: ThemeToggleProps) {
  const { theme, setTheme, computedTheme } = useTheme();

  const themes: { value: Theme; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
    { value: 'light', label: 'Light', icon: SunIcon },
    { value: 'github-dark', label: 'GitHub Dark', icon: MoonIcon },
    { value: 'printfarmer-dark', label: 'PrintFarmer Dark', icon: MoonIcon },
    { value: 'system', label: 'System', icon: MonitorIcon },
  ];

  const sizeClasses = {
    sm: 'p-1.5 text-sm',
    md: 'p-2 text-base',
    lg: 'p-3 text-lg'
  } as const;

  const iconSizes = {
    sm: 'h-3 w-3',
    md: 'h-4 w-4',
    lg: 'h-5 w-5'
  } as const;

  if (variant === 'dropdown') {
    return (
      <div className={`relative ${className ?? ''}`}>
        <select
          value={theme}
          onChange={(e) => setTheme(e.target.value as Theme)}
          className={`appearance-none bg-pf-panel border border-pf-border rounded-lg ${sizeClasses[size]} pr-8 text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent focus:border-transparent hover:bg-pf-bg-2 transition-colors duration-200`}
          aria-label="Select theme"
        >
          {themes.map(({ value, label }) => (
            <option key={value} value={value} className="bg-pf-panel text-pf-text-primary">
              {label}
            </option>
          ))}
        </select>
        <div className="absolute inset-y-0 right-0 flex items-center pr-2 pointer-events-none">
          <svg className={`${iconSizes[size]} text-pf-text-secondary`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
          </svg>
        </div>
      </div>
    );
  }

  if (variant === 'buttons') {
    const groupName = 'theme-toggle-group';
    return (
      <div role="radiogroup" data-testid="theme-radiogroup" className={`flex bg-pf-panel border border-pf-border rounded-lg ${className ?? ''}`} aria-label="Theme selection">
        {themes.map(({ value, label, icon: Icon }) => (
          <label
            key={value}
            data-testid={`theme-option-${value}`}
            className={`cursor-pointer first:rounded-l-lg last:rounded-r-lg border-r last:border-r-0 border-pf-border ${sizeClasses[size]} flex items-center space-x-2 transition-all duration-200 ${theme === value ? 'bg-pf-accent text-white' : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'}`}
          >
            <input
              type="radio"
              name={groupName}
              value={value}
              checked={theme === value}
              onChange={() => setTheme(value)}
              className="sr-only"
              aria-label={`Switch to ${label.toLowerCase()} theme`}
            />
            <Icon className={iconSizes[size]} aria-hidden="true" />
            {showLabels && <span className="hidden sm:inline">{label}</span>}
          </label>
        ))}
      </div>
    );
  }

  // Compact variant - cycle through themes
  const currentTheme = themes.find(t => t.value === theme) || themes[0];
  const computedThemeLabel = themes.find(t => t.value === computedTheme)?.label || computedTheme;
  const Icon = currentTheme.icon;

  return (
    <button
      type="button"
      onClick={() => {
        const currentIndex = themes.findIndex(t => t.value === theme);
        const nextIndex = (currentIndex + 1) % themes.length;
        setTheme(themes[nextIndex].value);
      }}
      className={`${sizeClasses[size]} ${className ?? ''} inline-flex items-center space-x-2 bg-pf-panel border border-pf-border rounded-lg text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2 focus:outline-none focus:ring-2 focus:ring-pf-accent focus:border-transparent transition-all duration-200`}
      aria-label={`Current theme: ${currentTheme.label}. Click to cycle themes.`}
      title={`Current: ${currentTheme.label} (${computedThemeLabel}). Click to change.`}
    >
      <Icon className={iconSizes[size]} />
      {showLabels && (
        <span className="hidden sm:inline">
          {currentTheme.label}
          {theme === 'system' && (
            <span className="text-xs opacity-75 ml-1">({computedTheme})</span>
          )}
        </span>
      )}
    </button>
  );
}