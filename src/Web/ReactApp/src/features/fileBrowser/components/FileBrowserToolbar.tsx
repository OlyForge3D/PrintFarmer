import { Fragment, ReactNode } from 'react';
import { SearchField, SortControl, Toolbar, ToolbarField } from '@/common/components/ui';
import { FileBrowserViewModeToggle } from '@/common/components/FileBrowserViewModeToggle';
import { type SortOrder, type ViewMode } from '../types';

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
  filterActions?: ReactNode;
  extraActions?: ReactNode;
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
  extraActions,
}: FileBrowserToolbarProps) => {
  return (
    <Toolbar aria-label="File controls" labelled data-tour="gcode-toolbar">
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

      {filterActions && (
        <Toolbar.Group aria-label="File filters">
          {filterActions}
        </Toolbar.Group>
      )}

      <ToolbarField label="Sort" htmlFor="file-browser-sort">
        <SortControl
          id="file-browser-sort"
          aria-label="Sort by"
          value={sortBy}
          onChange={(event) => onSortChange(event.target.value)}
          direction={sortOrder}
          onDirectionChange={onToggleSortOrder}
        >
            {sortOptions.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
        </SortControl>
      </ToolbarField>

      <Toolbar.Spacer />

      <FileBrowserViewModeToggle viewMode={viewMode} onViewModeChange={onViewModeChange} />
      {extraActions && <Toolbar.Group><Fragment>{extraActions}</Fragment></Toolbar.Group>}
    </Toolbar>
  );
};