import { useState, useCallback, useMemo, useRef, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import clsx from 'clsx';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Input, Select, Checkbox, Spinner, Badge } from '@/common/components/ui';
import { SearchIcon, FileIcon, FilterIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import type { GcodeFile } from '@/types/api';

const PAGE_SIZE = 24;

type SortOption = 'date-desc' | 'date-asc' | 'name-asc' | 'name-desc';

const SORT_CONFIG: Record<SortOption, { sortBy: string; sortOrder: string; label: string }> = {
  'date-desc': { sortBy: 'date', sortOrder: 'desc', label: 'Newest first' },
  'date-asc': { sortBy: 'date', sortOrder: 'asc', label: 'Oldest first' },
  'name-asc': { sortBy: 'name', sortOrder: 'asc', label: 'Name A–Z' },
  'name-desc': { sortBy: 'name', sortOrder: 'desc', label: 'Name Z–A' },
};

interface TagInfo {
  id: string;
  name: string;
  color?: string;
}

export interface FilePickerModalProps {
  isOpen: boolean;
  onClose: () => void;
  /** Called with the selected files when user confirms selection */
  onSelect: (files: GcodeFile[]) => void;
  /** File IDs to exclude from the picker (e.g., already added to project) */
  excludeIds?: string[];
  /** Modal title override */
  title?: string;
  /** Whether to allow selecting multiple files (default: true) */
  multiple?: boolean;
}

/**
 * Wrapper that remounts the inner content each time the modal opens,
 * so all state resets cleanly without calling setState inside useEffect.
 */
export function FilePickerModal({ isOpen, onClose, title, ...rest }: FilePickerModalProps) {
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={title ?? 'Select Files'}
      size="full"
      maxHeight="max-h-[85vh]"
    >
      {isOpen && <FilePickerContent onClose={onClose} {...rest} />}
    </Modal>
  );
}

function FilePickerContent({
  onClose,
  onSelect,
  excludeIds = [],
  multiple = true,
}: Omit<FilePickerModalProps, 'isOpen' | 'title'>) {
  const [search, setSearch] = useState('');
  const [sort, setSort] = useState<SortOption>('date-desc');
  const [selectedTagIds, setSelectedTagIds] = useState<string[]>([]);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [allFiles, setAllFiles] = useState<GcodeFile[]>([]);
  const [currentPage, setCurrentPage] = useState(1);
  const [hasMore, setHasMore] = useState(true);
  const [showFilters, setShowFilters] = useState(false);
  const searchTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const filesCacheRef = useRef<Map<string, GcodeFile>>(new Map());

  // Debounce search input
  useEffect(() => {
    if (searchTimerRef.current) clearTimeout(searchTimerRef.current);
    searchTimerRef.current = setTimeout(() => {
      setDebouncedSearch(search);
      setAllFiles([]);
      setCurrentPage(1);
      setHasMore(true);
    }, 300);
    return () => { if (searchTimerRef.current) clearTimeout(searchTimerRef.current); };
  }, [search]);

  // Reset pagination when filters/sort change
  const resetPagination = useCallback(() => {
    setAllFiles([]);
    setCurrentPage(1);
    setHasMore(true);
  }, []);

  const excludeSet = useMemo(() => new Set(excludeIds), [excludeIds]);

  const { sortBy, sortOrder } = SORT_CONFIG[sort];

  const { isFetching } = useQuery({
    queryKey: ['file-picker', debouncedSearch, sortBy, sortOrder, selectedTagIds, currentPage],
    queryFn: async () => {
      const params: Record<string, unknown> = {
        search: debouncedSearch || undefined,
        sortBy,
        sortOrder,
        page: currentPage,
        pageSize: PAGE_SIZE,
      };
      if (selectedTagIds.length > 0) {
        params.tagIds = selectedTagIds;
      }
      const result = await apiClient.getGcodeFilesQuery(params);
      const files = result.files || [];

      // Cache files for selection lookup
      for (const f of files) {
        filesCacheRef.current.set(f.id, f);
      }

      const totalPages = result.totalPages ?? 1;
      setHasMore(currentPage < totalPages);

      setAllFiles(prev => {
        if (currentPage === 1) return files;
        // Append new page, deduplicate
        const existingIds = new Set(prev.map(f => f.id));
        const newFiles = files.filter((f: GcodeFile) => !existingIds.has(f.id));
        return [...prev, ...newFiles];
      });

      return result;
    },
    enabled: true,
    staleTime: 30_000,
  });

  // Fetch available tags for filter dropdown
  const { data: availableTags = [] } = useQuery({
    queryKey: ['tags-for-picker'],
    queryFn: async () => {
      const raw = await apiClient.getTags();
      return raw.map(t => ({
        id: String(t.id ?? ''),
        name: String(t.name ?? ''),
        color: t.color ? String(t.color) : undefined,
      })) as TagInfo[];
    },
    enabled: true,
    staleTime: 300_000,
  });

  // Extract unique materials from loaded files for filter
  const materialOptions = useMemo(() => {
    const materials = new Set<string>();
    for (const f of allFiles) {
      if (f.extractedMaterial) materials.add(f.extractedMaterial);
    }
    return [...materials].sort();
  }, [allFiles]);

  // Files visible after excluding already-added
  const visibleFiles = useMemo(
    () => allFiles.filter(f => !excludeSet.has(f.id)),
    [allFiles, excludeSet]
  );

  const toggleFile = (fileId: string) => {
    if (!multiple) {
      const file = filesCacheRef.current.get(fileId);
      if (file) onSelect([file]);
      return;
    }
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (next.has(fileId)) next.delete(fileId);
      else next.add(fileId);
      return next;
    });
  };

  const toggleSelectAll = () => {
    const visibleIds = visibleFiles.map(f => f.id);
    const allSelected = visibleIds.every(id => selectedIds.has(id));
    if (allSelected) {
      setSelectedIds(prev => {
        const next = new Set(prev);
        for (const id of visibleIds) next.delete(id);
        return next;
      });
    } else {
      setSelectedIds(prev => {
        const next = new Set(prev);
        for (const id of visibleIds) next.add(id);
        return next;
      });
    }
  };

  const handleConfirm = () => {
    const files: GcodeFile[] = [];
    for (const id of selectedIds) {
      const file = filesCacheRef.current.get(id);
      if (file) files.push(file);
    }
    onSelect(files);
  };

  const handleLoadMore = () => {
    setCurrentPage(prev => prev + 1);
  };

  const handleSortChange = (value: string) => {
    setSort(value as SortOption);
    resetPagination();
  };

  const handleTagToggle = (tagId: string) => {
    setSelectedTagIds(prev =>
      prev.includes(tagId) ? prev.filter(id => id !== tagId) : [...prev, tagId]
    );
    resetPagination();
  };

  const allVisibleSelected = visibleFiles.length > 0 && visibleFiles.every(f => selectedIds.has(f.id));
  const someVisibleSelected = visibleFiles.some(f => selectedIds.has(f.id));
  const selectedCount = selectedIds.size;

  const footer = multiple ? (
    <div className="flex items-center justify-between w-full">
      <span className="text-sm text-pf-text-secondary">
        {selectedCount > 0 ? `${selectedCount} file${selectedCount !== 1 ? 's' : ''} selected` : 'No files selected'}
      </span>
      <div className="flex gap-2">
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button
          variant="primary"
          onClick={handleConfirm}
          disabled={selectedCount === 0}
        >
          Add {selectedCount > 0 ? selectedCount : ''} File{selectedCount !== 1 ? 's' : ''}
        </Button>
      </div>
    </div>
  ) : undefined;

  return (
    <>
      <div className="space-y-3">
        {/* Search + Sort bar */}
        <div className="flex gap-2 items-center flex-wrap">
          <div className="relative flex-1 min-w-50">
            <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-pf-text-tertiary" />
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search by file name..."
              className="pl-9"
              aria-label="Search files"
            />
          </div>
          <Select
            value={sort}
            onChange={(e) => handleSortChange(e.target.value)}
            containerClassName="w-40"
            aria-label="Sort files"
          >
            {Object.entries(SORT_CONFIG).map(([key, cfg]) => (
              <option key={key} value={key}>{cfg.label}</option>
            ))}
          </Select>
          <Button
            variant={showFilters ? 'primary' : 'secondary'}
            size="sm"
            onClick={() => setShowFilters(prev => !prev)}
            iconLeft={<FilterIcon />}
            aria-expanded={showFilters}
            aria-controls="file-picker-filters"
          >
            Filters
            {selectedTagIds.length > 0 && (
              <Badge variant="primary" size="sm" className="ml-1">{selectedTagIds.length}</Badge>
            )}
          </Button>
        </div>

        {/* Expandable filter panel */}
        {showFilters && (
          <div id="file-picker-filters" className="p-3 bg-pf-bg-2 border border-pf-border rounded-lg space-y-2">
            {/* Tags */}
            {availableTags.length > 0 && (
              <div>
                <label className="block text-xs font-medium text-pf-text-secondary mb-1">Tags</label>
                <div className="flex flex-wrap gap-1.5">
                  {availableTags.map(tag => {
                    const isActive = selectedTagIds.includes(tag.id);
                    return (
                      <Button
                        key={tag.id}
                        type="button"
                        variant="unstyled"
                        onClick={() => handleTagToggle(tag.id)}
                        className={clsx(
                          'inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs border transition-colors cursor-pointer',
                          isActive
                            ? 'bg-pf-accent text-white border-pf-accent'
                            : 'bg-pf-bg-0 text-pf-text-primary border-pf-border hover:border-pf-accent'
                        )}
                        aria-pressed={isActive}
                      >
                        {tag.color && (
                          <span
                            className="w-2.5 h-2.5 rounded-full inline-block shrink-0"
                            style={{ backgroundColor: tag.color }}
                            aria-hidden="true"
                          />
                        )}
                        {tag.name}
                      </Button>
                    );
                  })}
                </div>
              </div>
            )}
            {/* Materials (derived from loaded files) */}
            {materialOptions.length > 0 && (
              <div>
                <label className="block text-xs font-medium text-pf-text-secondary mb-1">Material (from loaded files)</label>
                <div className="flex flex-wrap gap-1.5">
                  {materialOptions.map(mat => (
                    <span key={mat} className="inline-flex items-center px-2 py-0.5 rounded-full text-xs bg-pf-bg-0 text-pf-text-primary border border-pf-border">
                      {mat}
                    </span>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}

        {/* Select all + count */}
        {multiple && visibleFiles.length > 0 && (
          <div className="flex items-center gap-3 px-1">
            <Checkbox
              checked={allVisibleSelected}
              indeterminate={someVisibleSelected && !allVisibleSelected}
              onChange={toggleSelectAll}
              label="Select all"
              aria-label={allVisibleSelected ? 'Deselect all visible files' : 'Select all visible files'}
            />
            <span className="text-xs text-pf-text-tertiary">
              {visibleFiles.length} file{visibleFiles.length !== 1 ? 's' : ''} shown
            </span>
          </div>
        )}

        {/* Thumbnail grid */}
        <div
          className="grid gap-3"
          style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))' }}
          role="list"
          aria-label="Available files"
        >
          {visibleFiles.map(file => {
            const isSelected = selectedIds.has(file.id);
            return (
              <Button
                key={file.id}
                type="button"
                variant="unstyled"
                onClick={() => toggleFile(file.id)}
                className={clsx(
                  'group relative rounded-lg border p-2 text-left transition-all cursor-pointer',
                  'hover:border-pf-accent hover:shadow-md',
                  'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
                  isSelected
                    ? 'border-pf-accent bg-pf-accent/5 ring-1 ring-pf-accent'
                    : 'border-pf-border bg-pf-bg-0'
                )}
                aria-pressed={isSelected}
                aria-label={`${isSelected ? 'Deselect' : 'Select'} ${file.name || file.fileName}`}
              >
                {/* Checkbox overlay */}
                {multiple && (
                  <div className={clsx(
                    'absolute top-1.5 left-1.5 z-10 transition-opacity',
                    isSelected ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'
                  )}>
                    <div className={clsx(
                      'w-5 h-5 rounded-sm border-2 flex items-center justify-center',
                      isSelected
                        ? 'bg-pf-accent border-pf-accent text-white'
                        : 'bg-pf-bg-0/80 border-pf-border'
                    )}>
                      {isSelected && (
                        <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                        </svg>
                      )}
                    </div>
                  </div>
                )}

                {/* Thumbnail */}
                <div className="aspect-square rounded bg-pf-bg-2 mb-2 overflow-hidden flex items-center justify-center">
                  {file.thumbnailUrl ? (
                    <img
                      src={file.thumbnailUrl}
                      alt=""
                      className="w-full h-full object-cover"
                      loading="lazy"
                    />
                  ) : (
                    <FileIcon className="w-10 h-10 text-pf-text-tertiary" />
                  )}
                </div>

                {/* File info */}
                <div className="space-y-0.5 min-w-0">
                  <p className="text-xs font-medium text-pf-text-primary truncate" title={file.name || file.fileName}>
                    {file.name || file.fileName}
                  </p>
                  <div className="flex items-center gap-1 flex-wrap">
                    {file.extractedMaterial && (
                      <span className="text-[10px] px-1 py-px rounded bg-pf-bg-2 text-pf-text-secondary">
                        {file.extractedMaterial}
                      </span>
                    )}
                    {file.extractedPrintTime != null && file.extractedPrintTime > 0 && (
                      <span className="text-[10px] text-pf-text-tertiary">
                        {file.extractedPrintTime >= 60
                          ? `${Math.floor(file.extractedPrintTime / 60)}h${Math.round(file.extractedPrintTime % 60)}m`
                          : `${Math.round(file.extractedPrintTime)}m`}
                      </span>
                    )}
                  </div>
                  {file.tags && file.tags.length > 0 && (
                    <div className="flex gap-0.5 flex-wrap mt-0.5">
                      {file.tags.slice(0, 2).map(tag => (
                        <span
                          key={tag.id}
                          className="text-[9px] px-1 rounded-full border border-pf-border text-pf-text-tertiary"
                        >
                          {tag.name}
                        </span>
                      ))}
                      {file.tags.length > 2 && (
                        <span className="text-[9px] text-pf-text-tertiary">+{file.tags.length - 2}</span>
                      )}
                    </div>
                  )}
                </div>
              </Button>
            );
          })}
        </div>

        {/* Empty state */}
        {!isFetching && visibleFiles.length === 0 && (
          <div className="text-center py-12 text-pf-text-tertiary">
            <FileIcon className="w-12 h-12 mx-auto mb-3 opacity-40" />
            <p className="text-sm">
              {debouncedSearch || selectedTagIds.length > 0
                ? 'No files match your filters'
                : 'No G-code files available'}
            </p>
          </div>
        )}

        {/* Loading / Load More */}
        {isFetching && (
          <div className="flex justify-center py-4">
            <Spinner className="w-6 h-6" />
          </div>
        )}
        {!isFetching && hasMore && visibleFiles.length > 0 && (
          <div className="flex justify-center pt-2 pb-1">
            <Button variant="secondary" size="sm" onClick={handleLoadMore}>
              Load more files
            </Button>
          </div>
        )}
      </div>

      {/* Footer */}
      {footer && (
        <div className="mt-4 pt-3 border-t border-pf-border">
          {footer}
        </div>
      )}
    </>
  );
}
