import React from 'react';
import { mdiViewAgenda, mdiViewGrid, mdiViewList } from '@mdi/js';
import { Button } from '@/common/components/ui';

export type FileBrowserViewMode = 'explorer' | 'grid' | 'list';

interface FileBrowserViewModeToggleProps {
  viewMode: FileBrowserViewMode;
  onViewModeChange: (mode: FileBrowserViewMode) => void;
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

export const FileBrowserViewModeToggle: React.FC<FileBrowserViewModeToggleProps> = ({
  viewMode,
  onViewModeChange
}) => {
  const modes: Array<{ mode: FileBrowserViewMode; icon: string; title: string }> = [
    { mode: 'explorer', icon: mdiViewAgenda, title: 'Explorer view' },
    { mode: 'grid', icon: mdiViewGrid, title: 'Grid view' },
    { mode: 'list', icon: mdiViewList, title: 'List view' },
  ];

  return (
    <div className="inline-flex gap-0 p-1">
      {modes.map((item) => (
        <Button
          key={item.mode}
          onClick={() => onViewModeChange(item.mode)}
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
};
