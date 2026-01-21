import React from 'react';
import { Button } from './Button';
import { viewModeIcons, type ViewModeIconName, type ViewModeOption } from './viewModeIcons';

/**
 * Generic ViewToggle component props
 */
export interface ViewToggleProps<T extends string = string> {
  /** Current view mode */
  value: T;
  /** Callback when view changes */
  onChange: (mode: T) => void;
  /** Array of view mode options */
  options: ViewModeOption<T>[];
  /** Additional CSS classes for the container */
  className?: string;
  /** Button size - defaults to 'sm' */
  size?: 'sm' | 'md' | 'lg';
  /** Aria label for the group */
  ariaLabel?: string;
}

/**
 * Icon component that renders an MDI path
 */
function ViewIcon({ path, className = 'w-4 h-4' }: { path: string; className?: string }) {
  return (
    <svg 
      className={className} 
      viewBox="0 0 24 24"
      aria-hidden="true"
    >
      <path fill="currentColor" d={path} />
    </svg>
  );
}

/**
 * Resolves an icon to its path string
 */
function resolveIconPath(icon: string | ViewModeIconName): string {
  if (icon in viewModeIcons) {
    return viewModeIcons[icon as ViewModeIconName];
  }
  return icon;
}

/**
 * ViewToggle - Generic toggle between multiple view modes
 * 
 * A flexible, reusable component for switching between different view modes.
 * Accepts an array of options with mode values, icons, and titles.
 * 
 * @example
 * // Simple grid/table toggle
 * <ViewToggle
 *   value={view}
 *   onChange={setView}
 *   options={[
 *     { mode: 'grid', icon: 'grid', title: 'Grid view' },
 *     { mode: 'table', icon: 'table', title: 'Table view' },
 *   ]}
 * />
 * 
 * @example
 * // Multiple view modes with custom icons
 * <ViewToggle
 *   value={viewMode}
 *   onChange={setViewMode}
 *   options={[
 *     { mode: 'compact', icon: 'compact', title: 'Compact Cards' },
 *     { mode: 'collapsed', icon: 'collapsed', title: 'Collapsed View' },
 *     { mode: 'expandable', icon: 'expandable', title: 'Expandable Cards' },
 *     { mode: 'table', icon: 'quilt', title: 'Table View' },
 *   ]}
 * />
 */
export function ViewToggle<T extends string>({ 
  value, 
  onChange, 
  options,
  className = '',
  size = 'sm',
  ariaLabel = 'View mode toggle',
}: ViewToggleProps<T>) {
  return (
    <div 
      className={`inline-flex gap-0 ${className}`}
      role="group"
      aria-label={ariaLabel}
    >
      {options.map((option) => (
        <Button
          key={option.mode}
          type="button"
          onClick={() => onChange(option.mode)}
          variant={value === option.mode ? 'primary' : 'secondary'}
          size={size}
          title={option.title}
          className="px-2"
        >
          <ViewIcon path={resolveIconPath(option.icon)} />
        </Button>
      ))}
    </div>
  );
}

export default ViewToggle;
