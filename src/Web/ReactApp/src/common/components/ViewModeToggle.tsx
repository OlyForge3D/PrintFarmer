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
import { Button } from '@/common/components/ui';

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

function MdiIcon({ path, size = 'w-4 h-4' }: { path: string; size?: string }) {
  return (
    <svg
      className={size}
      viewBox="0 0 24 24"
      role="img"
    >
      <path fill="currentColor" d={path} />
    </svg>
  );
}

export function ViewModeToggle({ viewMode, onChange }: ViewModeToggleProps) {
  const coreModesRow: Array<{ mode: ViewMode; icon: string; title: string }> = [
    { mode: 'compact', icon: mdiViewGrid, title: 'Compact Cards' },
    { mode: 'collapsed', icon: mdiViewList, title: 'Collapsed Card View' },
    { mode: 'expandable', icon: mdiViewComfy, title: 'Expandable Cards' },
    { mode: 'table', icon: mdiViewQuilt, title: 'Table View' },
  ];

  const experimentalModesRow: Array<{ mode: ViewMode; icon: string; title: string }> = [
    { mode: 'glass', icon: mdiBlur, title: '✨ Glassmorphism' },
    { mode: 'segmented', icon: mdiViewSequential, title: '📂 Segmented Sections' },
    { mode: 'statusGlow', icon: mdiLightbulbOn, title: '🔆 Status Glow' },
    { mode: 'dashboard', icon: mdiGauge, title: '📊 Dashboard Gauges' },
    { mode: 'flip', icon: mdiFlipToBack, title: '🔄 Flip Card' },
    { mode: 'drawer', icon: mdiArrowExpandDown, title: '📥 Expandable Drawer' },
  ];

  return (
    <div className="flex flex-col gap-1">
      {/* Core view modes */}
      <div className="inline-flex gap-0 p-1">
        {coreModesRow.map((item) => (
          <Button
            key={item.mode}
            onClick={() => onChange(item.mode)}
            variant={viewMode === item.mode ? 'primary' : 'secondary'}
            size="md"
            title={item.title}
            type="button"
            className="px-3"
          >
            <MdiIcon path={item.icon} />
          </Button>
        ))}
      </div>
      
      {/* Experimental view modes */}
      <div className="inline-flex gap-0 p-1">
        {experimentalModesRow.map((item) => (
          <Button
            key={item.mode}
            onClick={() => onChange(item.mode)}
            variant={viewMode === item.mode ? 'primary' : 'secondary'}
            size="sm"
            title={item.title}
            type="button"
            className="px-2 text-xs"
          >
            <MdiIcon path={item.icon} size="w-3 h-3" />
          </Button>
        ))}
      </div>
    </div>
  );
}
