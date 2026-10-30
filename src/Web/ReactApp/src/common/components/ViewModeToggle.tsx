import { 
  mdiViewList, 
  mdiTable,
  mdiViewComfy, 
} from '@mdi/js';
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
  { mode: 'collapsed', icon: mdiViewList, title: 'Collapsed Card View' },
  { mode: 'detailed', icon: mdiViewComfy, title: 'Detailed Cards' },
  { mode: 'table', icon: mdiTable, title: 'Table View' },
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
