import { Button } from './Button';
import { viewModeIcons, type ViewModeIconName, type ViewModeOption } from './viewModeIcons';
import clsx from 'clsx';

const SELECTED_VIEW_VARIANT = 'primary';

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
  /** Button size - defaults to the legacy 'sm' size */
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
 *     { mode: 'detailed', icon: 'expandable', title: 'Detailed Cards' },
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
      className={clsx('pf-toolbar__group pf-toolbar__group--segmented', className)}
      role="group"
      aria-label={ariaLabel}
    >
      {options.map((option) => (
        <Button
          key={option.mode}
          type="button"
          onClick={() => onChange(option.mode)}
          variant={value === option.mode ? SELECTED_VIEW_VARIANT : 'secondary'}
          size={size}
          control
          title={option.title}
          aria-label={option.title}
          aria-pressed={value === option.mode}
          className={option.label ? 'px-2' : 'pf-control--icon'}
          iconLeft={option.label ? <ViewIcon path={resolveIconPath(option.icon)} className={size === 'lg' ? 'w-5 h-5' : 'w-4 h-4'} /> : undefined}
          iconCenter={!option.label ? <ViewIcon path={resolveIconPath(option.icon)} className={size === 'lg' ? 'w-5 h-5' : 'w-4 h-4'} /> : undefined}
        >
          {option.label && <span className="hidden md:inline">{option.label}</span>}
        </Button>
      ))}
    </div>
  );
}

export default ViewToggle;
