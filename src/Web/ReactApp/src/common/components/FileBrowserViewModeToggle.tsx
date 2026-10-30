import React from 'react';
import { mdiViewAgenda, mdiViewGrid } from '@mdi/js';
import { ViewToggle } from '@/common/components/ui';

export type FileBrowserViewMode = 'explorer' | 'grid';

interface FileBrowserViewModeToggleProps {
  viewMode: FileBrowserViewMode;
  onViewModeChange: (mode: FileBrowserViewMode) => void;
}

export const FileBrowserViewModeToggle: React.FC<FileBrowserViewModeToggleProps> = ({
  viewMode,
  onViewModeChange,
}) => {
  const modes: Array<{ mode: FileBrowserViewMode; icon: string; title: string }> = [
    { mode: 'explorer', icon: mdiViewAgenda, title: 'Explorer view' },
    { mode: 'grid', icon: mdiViewGrid, title: 'Grid view' },
  ];

  return (
    <ViewToggle value={viewMode} onChange={onViewModeChange} options={modes} ariaLabel="File view mode" size="md" />
  );
};
