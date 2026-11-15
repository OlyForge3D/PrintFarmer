import { ViewToggle, type ViewModeOption } from '@/common/components/ui';

export type ViewMode = 
  | 'collapsed' 
  | 'detailed'
  | 'table';

interface ViewModeToggleProps {
  viewMode: ViewMode;
  onChange: (mode: ViewMode) => void;
}

/**
 * Core view mode options for Printers page
 */
const coreViewModes: ViewModeOption<ViewMode>[] = [
  { mode: 'collapsed', kind: 'compactCards', title: 'Collapsed Card View' },
  { mode: 'detailed', kind: 'detailedCards', title: 'Detailed Cards' },
  { mode: 'table', kind: 'table', title: 'Table View' },
];

/**
 * ViewModeToggle - Printers page view mode selector
 *
 * Uses the generic ViewToggle component for the supported printer views.
 */
export function ViewModeToggle({ viewMode, onChange }: ViewModeToggleProps) {
  return (
      <ViewToggle
        value={viewMode}
        onChange={onChange}
        options={coreViewModes}
        size="md"
        ariaLabel="Core view modes"
      />
  );
}
