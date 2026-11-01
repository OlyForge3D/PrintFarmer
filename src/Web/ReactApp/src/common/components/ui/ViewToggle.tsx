import { Button } from './Button';
import { Tooltip } from './Tooltip';
import { viewKindIcons, type ViewModeOption } from './viewModeIcons';
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
 * ViewToggle - Generic toggle between multiple view modes
 * 
 * A flexible, reusable component for switching between different view modes.
 * Accepts an array of options with mode values, icons, and titles. Each option
 * is icon-only; its title is shown in a tooltip and remains the accessible name.
 * 
 * @example
 * // Simple grid/table toggle
 * <ViewToggle
 *   value={view}
 *   onChange={setView}
 *   options={[
 *     { mode: 'grid', kind: 'cards', title: 'Grid view' },
 *     { mode: 'table', kind: 'table', title: 'Table view' },
 *   ]}
 * />
 * 
 * @example
 * // Multiple view modes with custom icons
 * <ViewToggle
 *   value={viewMode}
 *   onChange={setViewMode}
 *   options={[
 *     { mode: 'compact', kind: 'compactCards', title: 'Compact Cards' },
 *     { mode: 'detailed', kind: 'detailedCards', title: 'Detailed Cards' },
 *     { mode: 'table', kind: 'table', title: 'Table View' },
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
        <Tooltip key={option.mode} content={option.title} ariaMode="hidden">
          <Button
            type="button"
            onClick={() => onChange(option.mode)}
            variant={value === option.mode ? SELECTED_VIEW_VARIANT : 'secondary'}
            size={size}
            control
            aria-label={option.title}
            aria-pressed={value === option.mode}
            className="pf-control--icon"
            iconCenter={<ViewIcon path={viewKindIcons[option.kind]} className={size === 'lg' ? 'w-5 h-5' : 'w-4 h-4'} />}
          />
        </Tooltip>
      ))}
    </div>
  );
}

export default ViewToggle;
