import { Fragment, ReactNode } from 'react';
import { Button, Input, Select } from '@/common/components/ui';
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
  extraActions,
}: FileBrowserToolbarProps) => {
  return (
    <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
      <div className="flex-1 min-w-0">
        <label htmlFor="file-browser-search" className="sr-only">
          Search files
        </label>
        <Input
          id="file-browser-search"
          type="search"
          value={search}
          placeholder="Search files"
          aria-label="Search files"
          onChange={(e) => onSearchChange(e.target.value)}
          className="min-w-[16rem]"
        />
      </div>

      <div className="flex flex-wrap items-center justify-end gap-2 lg:flex-nowrap">
        <label htmlFor="file-browser-sort" className="text-sm text-pf-text-secondary whitespace-nowrap">
          Sort
        </label>
        <Select
          id="file-browser-sort"
          aria-label="Sort files"
          value={sortBy}
          onChange={(e) => onSortChange(e.target.value)}
          className="min-w-[7rem]"
        >
          {sortOptions.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </Select>
        <Button
          type="button"
          variant="secondary"
          size="sm"
          aria-label={sortOrder === 'asc' ? 'Sort descending' : 'Sort ascending'}
          onClick={onToggleSortOrder}
        >
          {sortOrder === 'asc' ? '↑' : '↓'}
        </Button>

        {extraActions && <Fragment>{extraActions}</Fragment>}

        <FileBrowserViewModeToggle viewMode={viewMode} onViewModeChange={onViewModeChange} />
      </div>
    </div>
  );
};