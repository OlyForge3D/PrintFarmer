import { Fragment, ReactNode } from 'react';
import { SearchField, SortControl, Toolbar, ToolbarField } from '@/common/components/ui';
import { FileBrowserViewModeToggle } from '@/common/components/FileBrowserViewModeToggle';
import { type FileBrowserFilterActions, type SortOrder, type ViewMode } from '../types';

interface FileBrowserToolbarProps {
  search: string;
  onSearchChange: (value: string) => void;
  sortBy: string;
  sortOrder: SortOrder;
  sortOptions: Array<{ value: string; label: string }>;
  onSortChange: (sortBy: string) => void;
  onToggleSortOrder: () => void;
  viewMode: ViewMode;
  onViewModeChange: (mode: ViewMode) => void;
  filterActions?: FileBrowserFilterActions;
  folderActions?: ReactNode;
  extraActions?: ReactNode;
  hideSortControl?: boolean;
}

export const FileBrowserToolbar = ({
  search,
  onSearchChange,
  sortBy,
  sortOrder,
  sortOptions,
  onSortChange,
  onToggleSortOrder,
  viewMode,
  onViewModeChange,
  filterActions,
  folderActions,
  extraActions,
  hideSortControl = false,
}: FileBrowserToolbarProps) => {
  const controls = (trigger: ReactNode) => (
    <Toolbar.Controls>
      <ToolbarField label="Search" htmlFor="file-browser-search" className="min-w-0 flex-1">
        <SearchField
          id="file-browser-search"
          label="Search"
          value={search}
          placeholder="Search files…"
          onChange={(event) => onSearchChange(event.target.value)}
          onClear={() => onSearchChange('')}
        />
      </ToolbarField>

      {trigger}
      {folderActions}

      {!hideSortControl && <ToolbarField label="Sort" htmlFor="file-browser-sort">
        <SortControl
          id="file-browser-sort"
          aria-label="Sort by"
          value={sortBy}
          onChange={(event) => onSortChange(event.target.value)}
          direction={sortOrder}
          onDirectionChange={onToggleSortOrder}
          directionDisabled={!sortBy}
        >
            <option value="">Default order</option>
            {sortOptions.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
        </SortControl>
      </ToolbarField>}

      <Toolbar.Spacer />

      <FileBrowserViewModeToggle viewMode={viewMode} onViewModeChange={onViewModeChange} />
      {extraActions && <Toolbar.Group><Fragment>{extraActions}</Fragment></Toolbar.Group>}
    </Toolbar.Controls>
  );

  return (
    <Toolbar aria-label="File controls" labelled data-tour="gcode-toolbar">
      {filterActions ? filterActions(controls) : controls(null)}
    </Toolbar>
  );
};