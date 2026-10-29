import { useImperativeHandle, ReactNode, type Ref } from 'react';
import { FileBrowserToolbar } from './FileBrowserToolbar';
import { GridView } from './GridView';
import { ExplorerView } from './ExplorerView';
import { useFileBrowser } from '../useFileBrowser';
import { Button } from '@/common/components/ui';
import { getErrorMessage } from '@/common/utils/apiErrors';
import { type ColumnDef, type FileItem, type UseFileBrowserConfig, type ViewMode } from '../types';

interface FileBrowserProps<TDomain> {
  config: UseFileBrowserConfig<TDomain>;
  sortOptions?: Array<{ value: string; label: string }>;
  columns: ColumnDef[];
  renderItemActions?: (file: FileItem) => ReactNode;
  renderMetadata?: (file: FileItem) => ReactNode;
  renderCard?: (file: FileItem, isSelected: boolean, onToggle: () => void) => ReactNode;
  filterActions?: ReactNode;
  extraToolbarActions?: ReactNode;
  renderFolderNavigation?: (path: string, onNavigate: (path: string) => void) => ReactNode;
  viewMode?: ViewMode;
  onViewModeChange?: (mode: ViewMode) => void;
  /** Opt-in dense presentation: auto-fill grid of small cards and a 12px table. */
  compact?: boolean;
  ref?: Ref<FileBrowserHandle>;
}

export interface FileBrowserHandle {
  refetch: () => Promise<void>;
}

export function FileBrowser<TDomain>({
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
    filterActions,
    extraToolbarActions,
    renderFolderNavigation,
    viewMode,
    onViewModeChange,
    compact = false,
    ref,
  }: FileBrowserProps<TDomain>) {
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
          filterActions={filterActions}
          extraActions={extraToolbarActions}
        />

        {renderFolderNavigation?.(browser.currentPath, (path) => {
          browser.clearSelection();
          browser.navigate(path);
        })}
        {browser.error && (
          <div role="alert" className="flex items-center gap-2 text-sm text-pf-error">
            {getErrorMessage(browser.error, 'Failed to load files')}
            <Button size="sm" variant="secondary" onClick={() => void browser.refetch()}>Retry files</Button>
          </div>
        )}

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
            page={browser.page}
            totalPages={browser.totalPages}
            onPageChange={browser.setPage}
            isBusy={browser.isMutating}
            compact={compact}
          />
        ) : (
          <ExplorerView
            showFolderNavigation={!renderFolderNavigation}
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
            compact={compact}
          />
        )}
        </div>
      </div>
    );
}