import { useId, useImperativeHandle, useState, ReactNode, type Ref } from 'react';
import { FileBrowserToolbar } from './FileBrowserToolbar';
import { GridView } from './GridView';
import { ExplorerView } from './ExplorerView';
import { useFileBrowser } from '../useFileBrowser';
import { Button, ToolbarField } from '@/common/components/ui';
import { getErrorMessage } from '@/common/utils/apiErrors';
import { FolderIcon } from '@/common/components/icons/MdiIcons';
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
  /** Place selected-file actions after the filter toolbar instead of inside it. */
  extraToolbarActionsPlacement?: 'toolbar' | 'below';
  renderFolderNavigation?: (
    path: string, onNavigate: (path: string) => void, controls: { id: string; expanded: boolean },
  ) => ReactNode;
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
    extraToolbarActionsPlacement = 'toolbar',
    renderFolderNavigation,
    viewMode,
    onViewModeChange,
    compact = false,
    ref,
  }: FileBrowserProps<TDomain>) {
    const browser = useFileBrowser({ ...config, viewMode, onViewModeChange });
    const folderControlsId = useId();
    const [foldersExpanded, setFoldersExpanded] = useState(false);

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
          filterActions={renderFolderNavigation ? <>
            {filterActions}
            <ToolbarField label="Folders" htmlFor={`${folderControlsId}-toggle`}>
              <Button id={`${folderControlsId}-toggle`} control
                variant={browser.currentPath ? 'primary' : 'secondary'}
                className="pf-control--icon" aria-label="Folders"
                iconCenter={<span aria-hidden="true"><FolderIcon className="h-4 w-4" /></span>}
                aria-expanded={foldersExpanded} aria-controls={folderControlsId}
                onClick={() => setFoldersExpanded(expanded => !expanded)}
                title={`Folders: ${browser.currentPath === '/' ? 'Root' : browser.currentPath || 'All folders'}`}
              />
            </ToolbarField>
          </> : filterActions}
          extraActions={extraToolbarActionsPlacement === 'toolbar' ? extraToolbarActions : undefined}
        />
        {extraToolbarActionsPlacement === 'below' && extraToolbarActions && (
          <div role="group" aria-label="Selected file actions" className="pf-inline-form">
            {extraToolbarActions}
          </div>
        )}

        {renderFolderNavigation?.(browser.currentPath, (path) => {
          browser.clearSelection();
          browser.navigate(path);
        }, { id: folderControlsId, expanded: foldersExpanded })}
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