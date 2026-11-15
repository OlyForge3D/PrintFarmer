import React from 'react';
import { ViewToggle, type ViewModeOption } from '@/common/components/ui';

export type FileBrowserViewMode = 'explorer' | 'grid';

interface FileBrowserViewModeToggleProps {
  viewMode: FileBrowserViewMode;
  onViewModeChange: (mode: FileBrowserViewMode) => void;
}

export const FileBrowserViewModeToggle: React.FC<FileBrowserViewModeToggleProps> = ({
  viewMode,
  onViewModeChange,
}) => {
  const modes: ViewModeOption<FileBrowserViewMode>[] = [
    { mode: 'explorer', kind: 'explorer', title: 'Explorer view' },
    { mode: 'grid', kind: 'cards', title: 'Grid view' },
  ];

  return (
    <ViewToggle value={viewMode} onChange={onViewModeChange} options={modes} ariaLabel="File view mode" size="md" />
  );
};
