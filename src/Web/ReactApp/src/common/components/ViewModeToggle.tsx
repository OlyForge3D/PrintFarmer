import React from 'react';
import { mdiViewList, mdiViewGrid, mdiViewComfy, mdiViewQuilt } from '@mdi/js';

type ViewMode = 'compact' | 'collapsed' | 'expandable' | 'table';

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
  const modes: Array<{ mode: ViewMode; icon: string; title: string }> = [
    { mode: 'compact', icon: mdiViewGrid, title: 'Compact Cards' },
    { mode: 'collapsed', icon: mdiViewList, title: 'Collapsed Card View' },
    { mode: 'expandable', icon: mdiViewComfy, title: 'Expandable Cards' },
    { mode: 'table', icon: mdiViewQuilt, title: 'Table View' },
  ];

  return (
    <div className="inline-flex border-b-2 border-pf-border">
      {modes.map((item, index) => (
        <button
          key={item.mode}
          onClick={() => onChange(item.mode)}
          className={`px-3 py-2 rounded-sm transition-colors border-r border-pf-border -mb-0.5 ${
            index === modes.length - 1 ? '' : ''
          } ${
            viewMode === item.mode
              ? 'bg-slate-500 text-white border-b-2 border-slate-500'
              : 'text-pf-text-secondary hover:text-pf-text-primary'
          }`}
          title={item.title}
          type="button"
        >
          <MdiIcon path={item.icon} />
        </button>
      ))}
    </div>
  );
}
