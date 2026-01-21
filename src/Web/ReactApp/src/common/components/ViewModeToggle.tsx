import React from 'react';
import { 
  mdiViewList, 
  mdiViewGrid, 
  mdiViewComfy, 
  mdiViewQuilt,
  mdiBlur,
  mdiViewSequential,
  mdiLightbulbOn,
  mdiGauge,
  mdiFlipToBack,
  mdiArrowExpandDown,
} from '@mdi/js';
import { ViewToggle, type ViewModeOption } from '@/common/components/ui';

export type ViewMode = 
  | 'compact' 
  | 'collapsed' 
  | 'expandable' 
  | 'table' 
  | 'glass' 
  | 'segmented' 
  | 'statusGlow' 
  | 'dashboard' 
  | 'flip' 
  | 'drawer';

interface ViewModeToggleProps {
  viewMode: ViewMode;
  onChange: (mode: ViewMode) => void;
}

/**
 * Core view mode options for Printers page
 */
const coreViewModes: ViewModeOption<ViewMode>[] = [
  { mode: 'compact', icon: mdiViewGrid, title: 'Compact Cards' },
  { mode: 'collapsed', icon: mdiViewList, title: 'Collapsed Card View' },
  { mode: 'expandable', icon: mdiViewComfy, title: 'Expandable Cards' },
  { mode: 'table', icon: mdiViewQuilt, title: 'Table View' },
];

/**
 * Experimental view mode options for Printers page
 */
const experimentalViewModes: ViewModeOption<ViewMode>[] = [
  { mode: 'glass', icon: mdiBlur, title: '✨ Glassmorphism' },
  { mode: 'segmented', icon: mdiViewSequential, title: '📂 Segmented Sections' },
  { mode: 'statusGlow', icon: mdiLightbulbOn, title: '🔆 Status Glow' },
  { mode: 'dashboard', icon: mdiGauge, title: '📊 Dashboard Gauges' },
  { mode: 'flip', icon: mdiFlipToBack, title: '🔄 Flip Card' },
  { mode: 'drawer', icon: mdiArrowExpandDown, title: '📥 Expandable Drawer' },
];

/**
 * ViewModeToggle - Printers page view mode selector
 * 
 * Uses the generic ViewToggle component with two rows of options:
 * - Core modes (compact, collapsed, expandable, table)
 * - Experimental modes (glass, segmented, statusGlow, dashboard, flip, drawer)
 */
export function ViewModeToggle({ viewMode, onChange }: ViewModeToggleProps) {
  return (
    <div className="flex flex-col gap-1">
      {/* Core view modes */}
      <ViewToggle
        value={viewMode}
        onChange={onChange}
        options={coreViewModes}
        size="md"
        className="p-1"
        ariaLabel="Core view modes"
      />
      
      {/* Experimental view modes */}
      <ViewToggle
        value={viewMode}
        onChange={onChange}
        options={experimentalViewModes}
        size="sm"
        className="p-1"
        ariaLabel="Experimental view modes"
      />
    </div>
  );
}
