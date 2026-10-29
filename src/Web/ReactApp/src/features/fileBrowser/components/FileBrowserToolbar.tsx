import { Fragment, ReactNode } from 'react';
import clsx from 'clsx';
import { Button, FormField, Input, Select } from '@/common/components/ui';
import { FileBrowserViewModeToggle } from '@/common/components/FileBrowserViewModeToggle';
import { type SortOrder, type ViewMode } from '../types';
import { FILE_TOOLBAR_CONTROL_CLASS } from '@/features/fileBrowser/fileBrowserStyles';

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
    <div className={clsx('flex flex-col gap-3 lg:flex-row lg:items-end', FILE_TOOLBAR_CONTROL_CLASS)} data-tour="gcode-toolbar">
      <FormField label="Search files" htmlFor="file-browser-search" className="flex-1 min-w-0">
        <Input
          id="file-browser-search"
          type="search"
          value={search}
          placeholder="Search files"
          aria-label="Search files"
          onChange={(e) => onSearchChange(e.target.value)}
          className="min-w-[16rem]"
        />
      </FormField>

      {filterActions && (
        <div className="flex flex-wrap items-end gap-2" role="group" aria-label="File filters">
          {filterActions}
        </div>
      )}

      <div className="flex flex-wrap items-end justify-end gap-2 lg:flex-nowrap">
        <FormField label="Sort" htmlFor="file-browser-sort">
          <Select
            id="file-browser-sort"
            aria-label="Sort files"
            value={sortBy}
            onChange={(e) => onSortChange(e.target.value)}
            className="min-w-28"
          >
            {sortOptions.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>
        </FormField>
        <Button
          type="button"
          variant="secondary"
          size="sm"
          aria-label={sortOrder === 'asc' ? 'Sort descending' : 'Sort ascending'}
          className="h-10 w-10 p-2"
          onClick={onToggleSortOrder}
        >
          {sortOrder === 'asc' ? '↑' : '↓'}
        </Button>

        {extraActions && <Fragment>{extraActions}</Fragment>}

        <FileBrowserViewModeToggle viewMode={viewMode} onViewModeChange={onViewModeChange} flush />
      </div>
    </div>
  );
};