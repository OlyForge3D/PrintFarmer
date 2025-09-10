import React from 'react';
import { Sun, Moon, Monitor } from 'lucide-react';
import { useTheme } from '@/contexts/ThemeContext';
import type { ThemeName } from '@/contexts/ThemeContext';

interface ThemeToggleProps {
  /** Show theme labels (defaults to false for compact mode) */
  showLabels?: boolean;
  /** Size of the toggle buttons */
  size?: 'sm' | 'md' | 'lg';
  /** Display variant */
  variant?: 'buttons' | 'dropdown' | 'compact';
  /** Additional CSS classes */
  className?: string;
}

/**
 * Theme toggle component with accessibility support
 */
export function ThemeToggle({ 
  showLabels = false, 
  size = 'md',
  variant = 'compact',
  className = ''
}: ThemeToggleProps) {
  const { theme, setTheme, computedTheme } = useTheme();

  const themes: { value: ThemeName; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
    { value: 'light', label: 'Light', icon: Sun },
    { value: 'dark', label: 'Dark', icon: Moon },
    { value: 'system', label: 'System', icon: Monitor },
  ];

  const sizeClasses = {
    sm: 'p-1.5 text-sm',
    md: 'p-2 text-base',
    lg: 'p-3 text-lg'
  };

  const iconSizes = {
    sm: 'h-3 w-3',
    md: 'h-4 w-4',
    lg: 'h-5 w-5'
  };

  if (variant === 'dropdown') {
    return (
      <div className={`relative ${className}`}>
        <select
          value={theme}
          onChange={(e) => setTheme(e.target.value as ThemeName)}
          className={`
            appearance-none bg-pf-panel border border-pf-border rounded-lg 
            ${sizeClasses[size]} pr-8 text-pf-text-primary
            focus:outline-none focus:ring-2 focus:ring-pf-accent focus:border-transparent
            hover:bg-pf-bg-2 transition-colors duration-200
          `}
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
    return (
      <div className={`flex bg-pf-panel border border-pf-border rounded-lg ${className}`} role="radiogroup" aria-label="Theme selection">
        {themes.map(({ value, label, icon: Icon }) => (
          <button
            key={value}
            onClick={() => setTheme(value)}
            className={`
              ${sizeClasses[size]} flex items-center space-x-2 transition-all duration-200
              first:rounded-l-lg last:rounded-r-lg border-r border-pf-border last:border-r-0
              focus:outline-none focus:ring-2 focus:ring-pf-accent focus:z-10
              ${theme === value 
                ? 'bg-pf-accent text-white' 
                : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'
              }
            `}
            role="radio"
            aria-checked={theme === value ? true : false}
            aria-label={`Switch to ${label.toLowerCase()} theme`}
          >
            <Icon className={iconSizes[size]} />
            {showLabels && <span className="hidden sm:inline">{label}</span>}
          </button>
        ))}
      </div>
    );
  }

  // Compact variant (default) - single toggle button
  const currentTheme = themes.find(t => t.value === theme) || themes[0];
  const Icon = currentTheme.icon;

  return (
    <button
      onClick={() => {
        // Cycle through themes: light -> dark -> system -> light
        const currentIndex = themes.findIndex(t => t.value === theme);
        const nextIndex = (currentIndex + 1) % themes.length;
        setTheme(themes[nextIndex].value);
      }}
      className={`
        ${sizeClasses[size]} ${className}
        inline-flex items-center space-x-2 bg-pf-panel border border-pf-border rounded-lg
        text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2
        focus:outline-none focus:ring-2 focus:ring-pf-accent focus:border-transparent
        transition-all duration-200
      `}
      aria-label={`Current theme: ${currentTheme.label}. Click to cycle themes.`}
      title={`Current: ${currentTheme.label} (${computedTheme}). Click to change.`}
  aria-pressed={(theme === 'dark') ? 'true' : 'false'}
    >
      <Icon className={iconSizes[size]} />
      {showLabels && (
        <span className="hidden sm:inline">
          {currentTheme.label}
          {theme === 'system' && (
            <span className="text-xs opacity-75 ml-1">
              ({computedTheme})
            </span>
          )}
        </span>
      )}
    </button>
  );
}

/**
 * Simple theme toggle hook for custom implementations
 */
// NOTE: Hook moved to separate file to satisfy react-refresh rule