/* eslint-disable local/pf-no-raw-html-controls */
import React, { useCallback, useRef, useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import { Badge } from '@/common/components/ui';
import {
  GearIcon,
  PackageIcon,
  LayersIcon,
  WrenchIcon,
  BellIcon,
  NetworkIcon,
  DatabaseIcon,
  UsersIcon,
  ChevronDownIcon,
  ServerIcon,
} from '@/common/components/icons/MdiIcons';
import type { SettingsCategory } from '@/features/settings/types';

/** Icon mapping for sidebar categories */
const CATEGORY_ICONS: Record<string, React.ReactNode> = {
  general: <GearIcon className="w-5 h-5" />,
  filament: <PackageIcon className="w-5 h-5" />,
  slicing: <LayersIcon className="w-5 h-5" />,
  hardware: <WrenchIcon className="w-5 h-5" />,
  notifications: <BellIcon className="w-5 h-5" />,
  integrations: <NetworkIcon className="w-5 h-5" />,
  system: <ServerIcon className="w-5 h-5" />,
  data: <DatabaseIcon className="w-5 h-5" />,
  users: <UsersIcon className="w-5 h-5" />,
};

interface SettingsSidebarProps {
  categories: SettingsCategory[];
  activeCategory: string;
  onCategoryChange: (categoryId: string) => void;
  /** IDs of categories matching current search query */
  matchingCategoryIds?: string[];
  /** Whether a search filter is active */
  isFiltering?: boolean;
}

export const SettingsSidebar: React.FC<SettingsSidebarProps> = ({
  categories,
  activeCategory,
  onCategoryChange,
  matchingCategoryIds,
  isFiltering = false,
}) => {
  const navRef = useRef<HTMLUListElement>(null);
  const itemRefs = useRef<Map<string, HTMLButtonElement>>(new Map());

  const visibleCategories = useMemo(() => {
    if (!isFiltering || !matchingCategoryIds) {
      return categories;
    }

    return categories.filter((category) => matchingCategoryIds.includes(category.id));
  }, [categories, isFiltering, matchingCategoryIds]);

  // Handle keyboard navigation within sidebar
  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLButtonElement>, index: number) => {
      let nextIndex: number | null = null;

      switch (e.key) {
        case 'ArrowDown':
          e.preventDefault();
          nextIndex = index < visibleCategories.length - 1 ? index + 1 : 0;
          break;
        case 'ArrowUp':
          e.preventDefault();
          nextIndex = index > 0 ? index - 1 : visibleCategories.length - 1;
          break;
        case 'Home':
          e.preventDefault();
          nextIndex = 0;
          break;
        case 'End':
          e.preventDefault();
          nextIndex = visibleCategories.length - 1;
          break;
      }

      if (nextIndex !== null) {
        const nextCategory = visibleCategories[nextIndex];
        const nextButton = itemRefs.current.get(nextCategory.id);
        nextButton?.focus();
      }
    },
    [visibleCategories]
  );

  // Store ref for each nav item
  const setItemRef = useCallback((id: string, el: HTMLButtonElement | null) => {
    if (el) {
      itemRefs.current.set(id, el);
    } else {
      itemRefs.current.delete(id);
    }
  }, []);

  const isMatchingCategory = useCallback(
    (categoryId: string) => {
      if (!isFiltering || !matchingCategoryIds) {
        return false;
      }

      return matchingCategoryIds.includes(categoryId);
    },
    [isFiltering, matchingCategoryIds]
  );

  return (
    <>
      {/* Desktop sidebar */}
      <nav
        className="hidden md:block w-60 shrink-0 border-r border-pf-border bg-pf-bg-0"
        aria-label="Settings categories"
      >
        <ul ref={navRef} role="list" className="py-2">
          {visibleCategories.map((category, index) => {
            const isActive = activeCategory === category.id;
            const isMatching = isMatchingCategory(category.id);

            return (
              <li key={category.id}>
                <button
                  ref={(el) => setItemRef(category.id, el)}
                  type="button"
                  onClick={() => onCategoryChange(category.id)}
                  onKeyDown={(e) => handleKeyDown(e, index)}
                  aria-current={isActive ? 'page' : undefined}
                  className={clsx(
                    'w-full flex items-center gap-3 px-4 py-2.5 text-sm font-medium transition-colors',
                    'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
                    isActive && 'bg-pf-accent-bg text-[var(--pf-on-accent)] border-l-2 border-pf-accent',
                    !isActive && 'text-pf-text-secondary hover:bg-pf-bg-1 hover:text-pf-text-primary border-l-2 border-transparent',
                    isMatching && !isActive && 'bg-pf-accent-bg text-[var(--pf-on-accent)]'
                  )}
                >
                  <span className="shrink-0" aria-hidden="true">
                    {CATEGORY_ICONS[category.id] ?? <GearIcon className="w-5 h-5" />}
                  </span>
                  <span className="min-w-0 flex-1 truncate">{category.label}</span>
                  {isMatching ? <Badge variant="info">Match</Badge> : null}
                </button>
              </li>
            );
          })}
        </ul>
      </nav>

      {/* Mobile dropdown */}
      <MobileCategorySelector
        categories={visibleCategories}
        activeCategory={activeCategory}
        onCategoryChange={onCategoryChange}
        matchingCategoryIds={matchingCategoryIds}
        isFiltering={isFiltering}
      />
    </>
  );
};

interface MobileCategorySelectorProps {
  categories: SettingsCategory[];
  activeCategory: string;
  onCategoryChange: (categoryId: string) => void;
  matchingCategoryIds?: string[];
  isFiltering?: boolean;
}

const MobileCategorySelector: React.FC<MobileCategorySelectorProps> = ({
  categories,
  activeCategory,
  onCategoryChange,
  matchingCategoryIds,
  isFiltering = false,
}) => {
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const [isOpen, setIsOpen] = useState(false);

  const activeLabel = categories.find((c) => c.id === activeCategory)?.label ?? 'Select';

  // Close dropdown when clicking outside
  useEffect(() => {
    if (!isOpen) return;

    const handleClickOutside = (e: MouseEvent) => {
      if (dropdownRef.current && !dropdownRef.current.contains(e.target as Node)) {
        setIsOpen(false);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [isOpen]);

  // Close on Escape
  useEffect(() => {
    if (!isOpen) return;

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setIsOpen(false);
        buttonRef.current?.focus();
      }
    };

    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [isOpen]);

  const handleSelect = (categoryId: string) => {
    onCategoryChange(categoryId);
    setIsOpen(false);
  };

  const isMatchingCategory = (categoryId: string) => {
    if (!isFiltering || !matchingCategoryIds) {
      return false;
    }

    return matchingCategoryIds.includes(categoryId);
  };

  return (
    <div ref={dropdownRef} className="md:hidden relative mb-4">
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setIsOpen(!isOpen)}
        aria-expanded={isOpen}
        aria-controls="settings-category-menu"
        aria-label={`Settings category: ${activeLabel}`}
        className={clsx(
          'w-full flex items-center justify-between gap-2 px-4 py-3',
          'bg-pf-bg-1 border border-pf-border rounded-lg',
          'text-sm font-medium text-pf-text-primary',
          'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent'
        )}
      >
        <span className="flex items-center gap-3">
          <span aria-hidden="true">
            {CATEGORY_ICONS[activeCategory] ?? <GearIcon className="w-5 h-5" />}
          </span>
          {activeLabel}
        </span>
        <ChevronDownIcon
          className={clsx('w-5 h-5 transition-transform', isOpen && 'rotate-180')}
        />
      </button>

      {isOpen && (
        <ul
          id="settings-category-menu"
          aria-label="Settings categories"
          className={clsx(
            'absolute z-50 w-full mt-1',
            'bg-pf-bg-0 border border-pf-border rounded-lg shadow-lg',
            'py-1 max-h-64 overflow-auto'
          )}
        >
          {categories.map((category) => {
            const isActive = activeCategory === category.id;
            const isMatching = isMatchingCategory(category.id);

            return (
              <li key={category.id}>
                <button
                  type="button"
                  onClick={() => handleSelect(category.id)}
                  aria-current={isActive ? 'page' : undefined}
                  className={clsx(
                    'w-full flex items-center gap-3 px-4 py-2.5 text-sm',
                    'focus:outline-hidden focus-visible:bg-pf-bg-1 focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
                    isActive && 'bg-pf-accent-bg text-[var(--pf-on-accent)] font-medium',
                    !isActive && 'text-pf-text-secondary hover:bg-pf-bg-1 hover:text-pf-text-primary',
                    isMatching && !isActive && 'bg-pf-accent-bg text-[var(--pf-on-accent)]'
                  )}
                >
                  <span aria-hidden="true">
                    {CATEGORY_ICONS[category.id] ?? <GearIcon className="w-5 h-5" />}
                  </span>
                  <span className="min-w-0 flex-1 truncate">{category.label}</span>
                  {isMatching ? <Badge variant="info">Match</Badge> : null}
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
};
