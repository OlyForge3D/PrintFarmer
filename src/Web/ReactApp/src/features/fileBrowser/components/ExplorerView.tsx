import { ReactNode, useRef, useState } from 'react';
import { Button, Checkbox } from '@/common/components/ui';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { type ColumnDef, type FileItem, type FolderNode, type SortOrder } from '../types';
import { ArrowLeftIcon, ArrowRightIcon, DeleteIcon, FolderOpenIcon, FolderIcon, PlusIcon } from '@/common/components/icons/MdiIcons';

interface ExplorerViewProps {
  folders: FolderNode[];
  files: FileItem[];
  selectedIds: string[];
  onToggle: (id: string) => void;
  onSelectAll: () => void;
  onNavigate: (path: string) => void;
  currentPath: string;
  onCreateDirectory?: () => void;
  onMoveFiles?: (fileIds: string[], targetPath: string) => Promise<void>;
  renderActions?: (file: FileItem) => ReactNode;
  sortBy: string;
  sortOrder: SortOrder;
  onSort: (columnKey: string) => void;
  page: number;
  totalPages: number;
  onPageChange: (page: number) => void;
  columns: ColumnDef[];
  isBusy?: boolean;
}

const FolderTree = ({
  nodes,
  currentPath,
  onNavigate,
  onCreateDirectory,
  selectedFileIds,
  onMoveFiles,
  isBusy,
  onCreateFolder,
  onDeleteFolder,
  onTogglePanel,
}: {
  nodes: FolderNode[];
  currentPath: string;
  onNavigate: (path: string) => void;
  onCreateDirectory?: () => void;
  selectedFileIds?: string[];
  onMoveFiles?: (fileIds: string[], targetPath: string) => Promise<void>;
  isBusy?: boolean;
  onCreateFolder?: (path: string) => void;
  onDeleteFolder?: (path: string) => void;
  onTogglePanel?: () => void;
}) => {
  return (
    <>
      <div className="border-b border-pf-border bg-pf-bg-1 px-3 py-2 flex items-center justify-between gap-2">
        <div className="text-xs font-semibold text-pf-text-secondary uppercase tracking-wider">Folders</div>
        <div className="flex items-center gap-1">
          {onTogglePanel && (
            <Button
              type="button"
              size="sm"
              variant="subtle"
              className="p-1 h-6 w-6"
              onClick={onTogglePanel}
              disabled={isBusy}
              title="Collapse folder panel"
              iconCenter={<ArrowLeftIcon className="h-4 w-4" />}
            />
          )}
          {onCreateDirectory && (
            <Button
              type="button"
              size="sm"
              variant="subtle"
              className="p-1 h-6 w-6"
              onClick={() => onCreateDirectory()}
              disabled={isBusy}
              title="Create folder in current directory"
              iconCenter={<PlusIcon className="h-4 w-4" />}
            />
          )}
        </div>
      </div>
      <ul className="space-y-1 p-2" aria-label="Folder tree">
        {nodes.map((node) => (
          <FolderTreeItem 
            key={node.path} 
            node={node} 
            depth={0} 
            currentPath={currentPath} 
            onNavigate={onNavigate}
            selectedFileIds={selectedFileIds}
            onMoveFiles={onMoveFiles}
            isBusy={isBusy}
            onCreateFolder={onCreateFolder}
            onDeleteFolder={onDeleteFolder}
          />
        ))}
      </ul>
    </>
  );
};

const FolderTreeItem = ({
  node,
  depth,
  currentPath,
  onNavigate,
  selectedFileIds,
  onMoveFiles,
  isBusy,
  onCreateFolder,
  onDeleteFolder,
  defaultExpanded = true,
}: {
  node: FolderNode;
  depth: number;
  currentPath: string;
  onNavigate: (path: string) => void;
  selectedFileIds?: string[];
  onMoveFiles?: (fileIds: string[], targetPath: string) => Promise<void>;
  isBusy?: boolean;
  onCreateFolder?: (path: string) => void;
  onDeleteFolder?: (path: string) => void;
  defaultExpanded?: boolean;
}) => {
  const [expanded, setExpanded] = useState(defaultExpanded);
  const [isDragOver, setIsDragOver] = useState(false);
  const [showContextMenu, setShowContextMenu] = useState(false);
  const contextMenuRef = useRef<HTMLDivElement>(null);
  const isCurrent = currentPath === node.path;
  const displayName = node.path === '/' ? '/' : node.name;
  const hasChildren = (node.children?.length || 0) > 0;

  const handleDragOver = (e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragOver(true);
  };

  const handleDragLeave = (e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragOver(false);
  };

  const handleDrop = async (e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragOver(false);

    const data = e.dataTransfer.getData('application/json');
    if (data && onMoveFiles && !isBusy) {
      try {
        const { fileIds } = JSON.parse(data);
        // Don't allow dropping files on the current path
        if (node.path !== currentPath) {
          await onMoveFiles(fileIds, node.path);
        }
      } catch (err) {
        console.error('Failed to parse drag data:', err);
      }
    }
  };

  const handleContextMenu = (e: React.MouseEvent<HTMLDivElement>) => {
    e.preventDefault();
    setShowContextMenu(true);
  };

  const handleCreateFolder = () => {
    setShowContextMenu(false);
    const folderName = prompt('Enter folder name:');
    if (folderName?.trim()) {
      onCreateFolder?.(node.path);
    }
  };

  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);

  const handleDeleteFolder = () => {
    setShowContextMenu(false);
    setShowDeleteConfirm(true);
  };

  const confirmDeleteFolder = () => {
    setShowDeleteConfirm(false);
    onDeleteFolder?.(node.path);
  };

  return (
    <li className="relative">
      <div
        className={`flex items-center gap-2 rounded px-2 py-1 text-sm transition-colors ${
          isDragOver ? 'bg-pf-primary/20 border border-pf-primary' : ''
        } ${isCurrent ? 'bg-pf-primary text-white' : !isDragOver ? 'hover:bg-pf-bg-2 text-pf-text' : ''}`}
        style={{ paddingLeft: depth * 12 + 8 }}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
        onContextMenu={handleContextMenu}
        ref={contextMenuRef}
      >
        {hasChildren ? (
          <Button
            type="button"
            variant="subtle"
            size="sm"
            className="p-0 h-5 w-5"
            aria-label={expanded ? 'Collapse folder' : 'Expand folder'}
            onClick={() => setExpanded((prev) => !prev)}
            iconCenter={expanded ? <ArrowRightIcon className="h-4 w-4 rotate-90" /> : <ArrowRightIcon className="h-4 w-4" />}
          />
        ) : (
          <span className="inline-block w-5" aria-hidden="true" />
        )}
        {isCurrent && expanded && hasChildren ? (
          <FolderOpenIcon className="h-4 w-4 shrink-0" aria-hidden="true" />
        ) : (
          <FolderIcon className="h-4 w-4 shrink-0" aria-hidden="true" />
        )}
        <Button
          type="button"
          variant="subtle"
          className="flex-1 justify-start text-left p-0 h-auto focus-visible:ring-2 focus-visible:ring-pf-primary"
          onClick={() => onNavigate(node.path)}
        >
          {displayName}
        </Button>
      </div>

      {/* Context Menu */}
      {showContextMenu && (
        <div className="absolute z-50 left-0 top-full mt-0.5 bg-pf-bg-2 border border-pf-border rounded-sm shadow-lg py-1 min-w-max">
          <Button
            type="button"
            variant="subtle"
            size="sm"
            onClick={handleCreateFolder}
            disabled={isBusy}
            className="w-full px-3 py-1.5 text-sm text-left text-pf-text-primary hover:bg-pf-bg-3 justify-start"
            iconLeft={<PlusIcon className="w-4 h-4" />}
          >
            New Folder
          </Button>
          <Button
            type="button"
            variant="subtle"
            size="sm"
            onClick={handleDeleteFolder}
            disabled={isBusy || node.path === '/'}
            className="w-full px-3 py-1.5 text-sm text-left text-pf-error hover:bg-pf-bg-3 justify-start"
            iconLeft={<DeleteIcon className="w-4 h-4" />}
          >
            Delete Folder
          </Button>
        </div>
      )}

      {hasChildren && expanded && (
        <ul className="space-y-1">
          {node.children?.map((child) => (
            <FolderTreeItem
              key={child.path}
              node={child}
              depth={depth + 1}
              currentPath={currentPath}
              onNavigate={onNavigate}
              selectedFileIds={selectedFileIds}
              onMoveFiles={onMoveFiles}
              isBusy={isBusy}
              onCreateFolder={onCreateFolder}
              onDeleteFolder={onDeleteFolder}
            />
          ))}
        </ul>
      )}

      {/* Delete folder confirmation */}
      <ConfirmationModal
        isOpen={showDeleteConfirm}
        title="Delete Folder?"
        message={`Delete folder "${displayName}"? This action cannot be undone.`}
        confirmButtonText="Delete"
        cancelButtonText="Cancel"
        isDangerous
        onConfirm={confirmDeleteFolder}
        onCancel={() => setShowDeleteConfirm(false)}
      />
    </li>
  );
};

export const ExplorerView = ({
  folders,
  files,
  selectedIds,
  onToggle,
  onSelectAll,
  onNavigate,
  currentPath,
  onCreateDirectory,
  onMoveFiles,
  renderActions,
  sortBy,
  sortOrder,
  onSort,
  page,
  totalPages,
  onPageChange,
  columns,
  isBusy,
}: ExplorerViewProps) => {
  const isAllSelected = selectedIds.length > 0 && selectedIds.length === files.length;
  const isIndeterminate = selectedIds.length > 0 && selectedIds.length !== files.length;
  const containerRef = useRef<HTMLDivElement>(null);
  const [treeWidth, setTreeWidth] = useState(220);
  const [isPanelCollapsed, setIsPanelCollapsed] = useState(false);

  // Helper to create drag data (extracted to avoid JSON.stringify in JSX)
  const createDragData = (fileIds: string[]): string => {
    return JSON.stringify({ fileIds });
  };

  const handleResizeDivider = (e: React.MouseEvent<HTMLDivElement>) => {
    const startX = e.clientX;
    const startWidth = treeWidth;

    const handleMouseMove = (moveEvent: MouseEvent) => {
      const diff = moveEvent.clientX - startX;
      const newWidth = Math.max(150, Math.min(startWidth + diff, 400)); // Min 150px, max 400px
      setTreeWidth(newWidth);
    };

    const handleMouseUp = () => {
      document.removeEventListener('mousemove', handleMouseMove);
      document.removeEventListener('mouseup', handleMouseUp);
    };

    document.addEventListener('mousemove', handleMouseMove);
    document.addEventListener('mouseup', handleMouseUp);
  };

  return (
    <div
      className="flex gap-0 h-full bg-pf-bg-0 rounded-lg border border-pf-border overflow-hidden"
      role="region"
      aria-label="Explorer view"
      aria-busy={isBusy}
      ref={containerRef}
    >
      {/* Left Pane: Folder Tree or Collapsed Bar */}
      {isPanelCollapsed ? (
        <div className="border-r border-pf-border bg-pf-bg-0 flex flex-col items-center py-2 px-1 shrink-0" style={{ width: '48px' }}>
          <Button
            type="button"
            size="sm"
            variant="subtle"
            className="p-1 h-8 w-8"
            onClick={() => setIsPanelCollapsed(false)}
            disabled={isBusy}
            title="Expand folder panel"
            iconCenter={<ArrowRightIcon className="h-4 w-4" />}
          />
        </div>
      ) : (
        <>
          <div
            className="border-r border-pf-border overflow-y-auto bg-pf-bg-0 flex flex-col shrink-0"
            style={{ width: `${treeWidth}px` }}
          >
            <div className="sticky top-0 shrink-0">
              <FolderTree 
                nodes={folders} 
                currentPath={currentPath} 
                onNavigate={onNavigate} 
                onCreateDirectory={onCreateDirectory} 
                selectedFileIds={selectedIds}
                onMoveFiles={onMoveFiles}
                isBusy={isBusy}
                onTogglePanel={() => setIsPanelCollapsed(true)}
                onCreateFolder={(path) => {
                  const folderName = prompt('Enter folder name:');
                  if (folderName?.trim()) {
                    // Create folder logic would go here
                    if (window.PrintFarmerDebug?.fileBrowser) {
                      console.log('Create folder:', path, folderName);
                    }
                  }
                }}
                onDeleteFolder={(path) => {
                  // Delete folder logic would go here
                  if (window.PrintFarmerDebug?.fileBrowser) {
                    console.log('Delete folder:', path);
                  }
                }}
              />
            </div>
          </div>

          {/* Resizable Divider - only show when panel is expanded */}
          <div
            className="w-1 bg-pf-border hover:bg-pf-accent active:bg-pf-accent transition-colors cursor-col-resize shrink-0"
            onMouseDown={handleResizeDivider}
            role="separator"
            aria-orientation="vertical"
            aria-label="Resize tree and list views"
          />
        </>
      )}

      {/* Right Pane: Files Table */}
      <div className="flex flex-col overflow-hidden bg-pf-bg-0 flex-1">
        {/* Breadcrumbs and Selection Count */}
        <div className="border-b border-pf-border bg-pf-bg-1 px-3 py-1 flex items-center justify-between">
          <nav className="flex items-center gap-1 text-xs flex-1" aria-label="File path">
            <Button
              type="button"
              variant="subtle"
              size="sm"
              className="p-0 h-auto font-semibold text-pf-text-secondary hover:text-pf-text"
              onClick={() => onNavigate('/')}
            >
              root
            </Button>
            {currentPath !== '/' && currentPath.split('/').filter(Boolean).map((segment, index, arr) => {
              const path = '/' + arr.slice(0, index + 1).join('/');
              const isLast = index === arr.length - 1;
              return (
                <div key={index} className="flex items-center gap-1">
                  <span className="text-pf-text-secondary">/</span>
                  <Button
                    type="button"
                    variant="subtle"
                    size="sm"
                    className={`p-0 h-auto ${isLast ? 'font-semibold text-pf-text' : 'text-pf-text-secondary hover:text-pf-text'}`}
                    onClick={() => onNavigate(path)}
                  >
                    {segment}
                  </Button>
                </div>
              );
            })}
          </nav>
          <div className="ml-4 shrink-0 text-xs text-pf-text-secondary whitespace-nowrap">
            {selectedIds.length > 0 ? `${selectedIds.length} selected` : ''}
          </div>
        </div>

        {/* Table */}
        <div className="flex-1 overflow-x-auto">
          <table className="w-full text-sm border-collapse" role="table" aria-label="Files list">
            <thead className="sticky top-0 bg-pf-bg-1 border-b border-pf-border">
              <tr>
                <th scope="col" className="w-10 px-3 py-2 text-left font-semibold text-pf-text-secondary text-xs uppercase tracking-wider">
                  <Checkbox
                    aria-label="Select all files"
                    checked={isAllSelected}
                    aria-checked={isIndeterminate ? 'mixed' : isAllSelected}
                    onChange={() => onSelectAll()}
                  />
                </th>
                {columns.map((column) => {
                  const sortable = column.sortable;
                  const isSorted = sortBy === column.key;
                  const ariaSort = isSorted ? (sortOrder === 'asc' ? 'ascending' : 'descending') : 'none';
                  return (
                    <th
                      key={column.key}
                      scope="col"
                      className={`px-3 py-2 text-left font-semibold text-pf-text-secondary text-xs uppercase tracking-wider ${column.align === 'right' ? 'text-right' : ''}`}
                      aria-sort={sortable ? ariaSort : undefined}
                    >
                      {sortable ? (
                        <Button
                          type="button"
                          variant="subtle"
                          size="sm"
                          className="inline-flex items-center gap-1 px-0 py-0 cursor-pointer hover:bg-pf-bg-2 transition-colors"
                          onClick={() => onSort(column.key)}
                          aria-label={`Sort by ${column.label}`}
                        >
                          {column.label}
                          {isSorted && <span aria-hidden="true">{sortOrder === 'asc' ? ' ↑' : ' ↓'}</span>}
                        </Button>
                      ) : (
                        <span className="text-pf-text-secondary">{column.label}</span>
                      )}
                    </th>
                  );
                })}
                <th scope="col" className="px-3 py-2 text-right font-semibold text-pf-text-secondary text-xs uppercase tracking-wider">
                  Actions
                </th>
              </tr>
            </thead>
            <tbody>
              {files.map((file) => {
                const isSelected = selectedIds.includes(file.id);
                const isDraggable = !file.isDirectory && !!onMoveFiles;

                const handleDragStart = (e: React.DragEvent<HTMLTableRowElement>) => {
                  if (!isDraggable) {
                    e.preventDefault();
                    return;
                  }
                  // If there are selected files, drag all of them (including the one being dragged)
                  if (selectedIds.length > 0) {
                    // Include the current file in the drag even if not selected
                    const filesToDrag = isSelected ? selectedIds : [file.id, ...selectedIds];
                    e.dataTransfer.setData('application/json', createDragData(filesToDrag));
                  } else {
                    // No selected files, drag only the one being dragged
                    e.dataTransfer.setData('application/json', createDragData([file.id]));
                  }
                  e.dataTransfer.effectAllowed = 'move';
                };

                return (
                  <tr
                    key={file.id}
                    draggable={isDraggable}
                    onDragStart={handleDragStart}
                    className={`border-b border-pf-border hover:bg-pf-bg-2 transition-colors ${
                      isSelected ? 'bg-pf-primary/5' : ''
                    } ${isDraggable ? 'cursor-grab active:cursor-grabbing' : ''}`}
                  >
                    <td className="px-3 py-2 w-10">
                      <Checkbox
                        aria-label={`Select ${file.fileName}`}
                        checked={isSelected}
                        onChange={() => onToggle(file.id)}
                      />
                    </td>
                    {columns.map((column) => (
                      <td
                        key={`${file.id}-${column.key}`}
                        className={`px-3 py-2 text-pf-text ${column.align === 'right' ? 'text-right' : ''}`}
                      >
                        {column.render
                          ? column.render(file)
                          : window.PrintFarmerDebug?.fileBrowser
                            ? (file as unknown as Record<string, ReactNode | string | number | undefined>)[
                                column.key
                              ] ?? '—'
                            : typeof (file as unknown as Record<string, ReactNode | string | number | undefined>)[
                                column.key
                              ] === 'string' || typeof (file as unknown as Record<string, ReactNode | string | number | undefined>)[column.key] === 'number'
                              ? (file as unknown as Record<string, ReactNode | string | number | undefined>)[
                                  column.key
                                ] ?? '—'
                              : '—'}
                      </td>
                    ))}
                    <td className="px-3 py-2 text-right">
                      {renderActions && (
                        <div className="flex justify-end gap-2">
                          {renderActions(file)}
                        </div>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        {/* Pagination */}
        {totalPages > 1 && (
          <div className="border-t border-pf-border bg-pf-bg-1 px-3 py-1 flex items-center justify-between text-xs text-pf-text-secondary">
            <div>
              Page {page} of {totalPages}
            </div>
            <div className="flex items-center gap-2">
              <Button
                type="button"
                size="sm"
                variant="subtle"
                disabled={page === 1 || isBusy}
                onClick={() => onPageChange(page - 1)}
                aria-label="Previous page"
                iconCenter={<ArrowLeftIcon className="h-4 w-4" />}
              />
              <Button
                type="button"
                size="sm"
                variant="subtle"
                disabled={page === totalPages || isBusy}
                onClick={() => onPageChange(page + 1)}
                aria-label="Next page"
                iconCenter={<ArrowRightIcon className="h-4 w-4" />}
              />
            </div>
          </div>
        )}
      </div>
    </div>
  );
};