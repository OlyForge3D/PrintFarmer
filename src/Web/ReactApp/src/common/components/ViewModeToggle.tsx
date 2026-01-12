import React from 'react';
import { mdiViewList, mdiViewGrid, mdiViewComfy, mdiViewQuilt } from '@mdi/js';
import { Button } from '@/common/components/ui';

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
    <div className="inline-flex gap-0 p-1">
      {modes.map((item) => (
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
  );
}
