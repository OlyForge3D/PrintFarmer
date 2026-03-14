import { forwardRef, useImperativeHandle, ReactNode, type Ref } from 'react';
import { FileBrowserToolbar } from './FileBrowserToolbar';
import { GridView } from './GridView';
import { ExplorerView } from './ExplorerView';
import { useFileBrowser } from '../useFileBrowser';
import { type ColumnDef, type FileItem, type UseFileBrowserConfig, type ViewMode } from '../types';

interface FileBrowserProps<TDomain> {
  config: UseFileBrowserConfig<TDomain>;
  sortOptions?: Array<{ value: string; label: string }>;
  columns: ColumnDef[];
  renderItemActions?: (file: FileItem) => ReactNode;
  renderMetadata?: (file: FileItem) => ReactNode;
  renderCard?: (file: FileItem, isSelected: boolean, onToggle: () => void) => ReactNode;
  extraToolbarActions?: ReactNode;
  viewMode?: ViewMode;
  onViewModeChange?: (mode: ViewMode) => void;
}

export interface FileBrowserHandle {
  refetch: () => Promise<void>;
}

export const FileBrowser = forwardRef<FileBrowserHandle, FileBrowserProps<unknown>>(
  function FileBrowser<TDomain = unknown>({
    config,
    sortOptions = [
      { value: 'name', label: 'Name' },
      { value: 'size', label: 'Size' },
      { value: 'date', label: 'Date' },
    ],
    columns,
    renderItemActions,
    renderMetadata,
    renderCard,
    extraToolbarActions,
    viewMode,
    onViewModeChange,
  }: FileBrowserProps<TDomain>, ref: Ref<FileBrowserHandle>) {
    const browser = useFileBrowser({ ...config, viewMode, onViewModeChange });

    useImperativeHandle(ref, () => ({
      refetch: browser.refetch,
    }), [browser.refetch]);

    const isBusy = browser.isLoading || browser.isMutating;

    return (
      <div
        className="flex flex-col gap-4"
        role="region"
        aria-label="File browser"
        aria-busy={isBusy}
      >
        <FileBrowserToolbar
          search={browser.search}
          onSearchChange={browser.setSearch}
          sortBy={browser.sortBy}
          sortOrder={browser.sortOrder}
          sortOptions={sortOptions}
          onSortChange={(value) => browser.setSort(value)}
          onToggleSortOrder={() => browser.setSort(browser.sortBy)}
          viewMode={browser.viewMode}
          onViewModeChange={browser.setViewMode}
          extraActions={extraToolbarActions}
        />

        <div className="sr-only" aria-live="polite">
          {browser.selectedIds.length} item{browser.selectedIds.length === 1 ? '' : 's'} selected
        </div>
        <div className="sr-only" aria-live="polite">
          {isBusy ? (browser.isMutating ? 'Applying changes…' : 'Loading files…') : 'Ready'}
        </div>

        <div data-tour="gcode-file-list">
        {browser.viewMode === 'grid' ? (
          <GridView
            files={browser.files}
            selectedIds={browser.selectedIds}
            onToggle={browser.toggleSelect}
            onSelectAll={browser.selectAll}
            renderItemActions={renderItemActions}
            renderMetadata={renderMetadata}
            renderCard={renderCard}
            isBusy={browser.isMutating}
          />
        ) : (
          <ExplorerView
            folders={browser.folders}
            files={browser.files}
            selectedIds={browser.selectedIds}
            onToggle={browser.toggleSelect}
            onSelectAll={browser.selectAll}
            onNavigate={(path) => browser.navigate(path)}
            currentPath={browser.currentPath}
            onCreateDirectory={
              config.canCreateDirectory && config.onCreateDirectory
                ? () => browser.createDirectory()
                : undefined
            }
            onMoveFiles={config.onMoveFiles ? browser.moveFiles : undefined}
            renderActions={renderItemActions}
            sortBy={browser.sortBy}
            sortOrder={browser.sortOrder}
            onSort={(key) => browser.setSort(key)}
            page={browser.page}
            totalPages={browser.totalPages}
            onPageChange={browser.setPage}
            columns={columns}
            isBusy={browser.isMutating}
          />
        )}
        </div>
      </div>
    );
  }
);