import { useState, useMemo, useCallback, useRef, useEffect } from 'react';
import clsx from 'clsx';
import { Button, Input, Spinner } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { SearchIcon, CubeIcon } from '@/common/components/icons/MdiIcons';

interface SearchablePickerModalProps<T> {
  isOpen: boolean;
  onClose: () => void;
  onSelect: (item: T) => void;
  items: T[];
  getItemId: (item: T) => string;
  getLabel: (item: T) => string;
  getSubLabel?: (item: T) => string;
  getThumbnail?: (item: T) => string | undefined;
  selectedId?: string;
  title?: string;
  searchPlaceholder?: string;
  emptyMessage?: string;
  isLoading?: boolean;
  className?: string;
}

const DEBOUNCE_MS = 300;

/**
 * Generic, reusable modal picker with search, grid layout, keyboard navigation,
 * and optional thumbnails. Designed to scale to hundreds or thousands of items.
 */
export function SearchablePickerModal<T>({
  isOpen,
  onClose,
  onSelect,
  items,
  getItemId,
  getLabel,
  getSubLabel,
  getThumbnail,
  selectedId,
  title = 'Select an item',
  searchPlaceholder = 'Search...',
  emptyMessage = 'No items match your search.',
  isLoading = false,
  className,
}: SearchablePickerModalProps<T>) {
  // Key-based reset: remount inner content when modal re-opens
  const resetKey = `${isOpen}-${selectedId}`;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={title}
      size="lg"
      closeOnEscape
      className={className}
    >
      <SearchablePickerContent<T>
        key={resetKey}
        onClose={onClose}
        onSelect={onSelect}
        items={items}
        getItemId={getItemId}
        getLabel={getLabel}
        getSubLabel={getSubLabel}
        getThumbnail={getThumbnail}
        selectedId={selectedId}
        title={title}
        searchPlaceholder={searchPlaceholder}
        emptyMessage={emptyMessage}
        isLoading={isLoading}
      />
    </Modal>
  );
}

interface SearchablePickerContentProps<T> {
  onClose: () => void;
  onSelect: (item: T) => void;
  items: T[];
  getItemId: (item: T) => string;
  getLabel: (item: T) => string;
  getSubLabel?: (item: T) => string;
  getThumbnail?: (item: T) => string | undefined;
  selectedId?: string;
  title: string;
  searchPlaceholder: string;
  emptyMessage: string;
  isLoading: boolean;
}

function SearchablePickerContent<T>({
  onClose,
  onSelect,
  items,
  getItemId,
  getLabel,
  getSubLabel,
  getThumbnail,
  selectedId,
  title,
  searchPlaceholder,
  emptyMessage,
  isLoading,
}: SearchablePickerContentProps<T>) {
  const [searchTerm, setSearchTerm] = useState('');
  const [debouncedTerm, setDebouncedTerm] = useState('');
  const [focusedId, setFocusedId] = useState<string | undefined>(selectedId);
  const searchInputRef = useRef<HTMLInputElement>(null);
  const gridRef = useRef<HTMLDivElement>(null);

  // Debounce search input
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedTerm(searchTerm), DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [searchTerm]);

  // Auto-focus search input on mount
  useEffect(() => {
    requestAnimationFrame(() => searchInputRef.current?.focus());
  }, []);

  const filteredItems = useMemo(() => {
    if (!debouncedTerm.trim()) return items;
    const lower = debouncedTerm.toLowerCase();
    return items.filter((item) => {
      const label = getLabel(item).toLowerCase();
      const sub = getSubLabel?.(item)?.toLowerCase() ?? '';
      return label.includes(lower) || sub.includes(lower);
    });
  }, [items, debouncedTerm, getLabel, getSubLabel]);

  const handleSelect = useCallback(
    (item: T) => {
      onSelect(item);
      onClose();
    },
    [onSelect, onClose],
  );

  const handleConfirm = useCallback(() => {
    if (!focusedId) return;
    const item = filteredItems.find((i) => getItemId(i) === focusedId);
    if (item) handleSelect(item);
  }, [focusedId, filteredItems, getItemId, handleSelect]);

  // Keyboard navigation inside the grid
  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (filteredItems.length === 0) return;

      const ids = filteredItems.map(getItemId);
      const currentIndex = focusedId ? ids.indexOf(focusedId) : -1;

      // Determine columns from the grid layout
      const gridEl = gridRef.current;
      const firstChild = gridEl?.firstElementChild as HTMLElement | null;
      const childWidth = firstChild?.offsetWidth ?? 0;
      const cols = gridEl && childWidth > 0
        ? Math.round(gridEl.offsetWidth / childWidth)
        : 3;

      const computeNextIndex = (): number => {
        switch (e.key) {
          case 'ArrowRight':
            e.preventDefault();
            return Math.min(currentIndex + 1, ids.length - 1);
          case 'ArrowLeft':
            e.preventDefault();
            return Math.max(currentIndex - 1, 0);
          case 'ArrowDown':
            e.preventDefault();
            return Math.min(currentIndex + cols, ids.length - 1);
          case 'ArrowUp':
            e.preventDefault();
            return Math.max(currentIndex - cols, 0);
          case 'Enter':
            e.preventDefault();
            handleConfirm();
            return currentIndex;
          default:
            return currentIndex;
        }
      };

      const nextIndex = computeNextIndex();

      if (nextIndex !== currentIndex && nextIndex >= 0) {
        setFocusedId(ids[nextIndex]);
        // Scroll the focused card into view
        const card = gridEl?.children[nextIndex] as HTMLElement | undefined;
        card?.scrollIntoView({ block: 'nearest' });
      }
    },
    [filteredItems, getItemId, focusedId, handleConfirm],
  );

  const footer = (
    <>
      <Button variant="secondary" onClick={onClose}>
        Cancel
      </Button>
      <Button variant="primary" disabled={!focusedId} onClick={handleConfirm}>
        Select
      </Button>
    </>
  );

  return (
    <>
      {/* Search bar */}
      <div className="relative mb-4">
        <div className="pointer-events-none absolute inset-y-0 left-0 flex items-center pl-3">
          <SearchIcon className="w-4 h-4 text-pf-text-muted" />
        </div>
        <Input
          ref={searchInputRef}
          type="text"
          value={searchTerm}
          onChange={(e) => setSearchTerm(e.target.value)}
          placeholder={searchPlaceholder}
          className="pl-9"
          aria-label="Search items"
        />
      </div>

      {/* Content area */}
      {isLoading ? (
        <div className="flex items-center justify-center py-16">
          <Spinner size="lg" />
        </div>
      ) : filteredItems.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-16 text-pf-text-muted">
          <SearchIcon className="w-8 h-8 mb-2 opacity-40" />
          <p className="text-sm">{emptyMessage}</p>
        </div>
      ) : (
        <div
          ref={gridRef}
          className="grid grid-cols-2 sm:grid-cols-3 gap-3 max-h-[50vh] overflow-y-auto pr-1"
          onKeyDown={handleKeyDown}
          tabIndex={0}
          role="listbox"
          aria-label={title}
        >
          {filteredItems.map((item) => {
            const id = getItemId(item);
            const label = getLabel(item);
            const subLabel = getSubLabel?.(item);
            const thumbnailUrl = getThumbnail?.(item);
            const isFocused = focusedId === id;

            return (
              <div
                key={id}
                role="option"
                aria-selected={isFocused ? true : false}
                className={clsx(
                  'flex flex-col items-center gap-2 rounded-lg border p-3 cursor-pointer transition-colors',
                  'hover:border-pf-accent-2/50 hover:bg-pf-bg-0/50',
                  isFocused
                    ? 'border-pf-accent-2 bg-pf-accent-bg ring-1 ring-pf-accent-2/30'
                    : 'border-pf-border bg-pf-bg-0',
                )}
                onClick={() => setFocusedId(id)}
                onDoubleClick={() => handleSelect(item)}
              >
                {/* Thumbnail or fallback icon */}
                <div className="w-12 h-12 rounded-md flex items-center justify-center overflow-hidden shrink-0 bg-pf-bg-1">
                  {thumbnailUrl ? (
                    <img
                      src={thumbnailUrl}
                      alt={label}
                      className="w-full h-full object-cover"
                    />
                  ) : (
                    <CubeIcon className="w-6 h-6 text-pf-text-muted" />
                  )}
                </div>

                {/* Label */}
                <div className="text-center min-w-0 w-full">
                  <p className="text-sm font-medium text-pf-text-primary truncate" title={label}>
                    {label}
                  </p>
                  {subLabel && (
                    <p className="text-xs text-pf-text-muted truncate" title={subLabel}>
                      {subLabel}
                    </p>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}

      {/* Result count */}
      {!isLoading && filteredItems.length > 0 && (
        <p className="text-xs text-pf-text-muted mt-3">
          {filteredItems.length === items.length
            ? `${items.length} item${items.length === 1 ? '' : 's'}`
            : `${filteredItems.length} of ${items.length} item${items.length === 1 ? '' : 's'}`}
        </p>
      )}

      {/* Footer */}
      <div className="flex justify-end gap-2 mt-4 pt-3 border-t border-pf-border">
        {footer}
      </div>
    </>
  );
}
