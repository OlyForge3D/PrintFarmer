/* eslint-disable local/pf-no-raw-html-controls */
import React, { useCallback, useRef, useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
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
import { SettingsMatchText } from '@/features/settings/components/SettingsMatchText';
import type { SettingsCategory } from '@/features/settings/types';

const CATEGORY_ICONS: Record<string, React.ReactNode> = {
  general: <GearIcon className="h-5 w-5" />,
  filament: <PackageIcon className="h-5 w-5" />,
  slicing: <LayersIcon className="h-5 w-5" />,
  hardware: <WrenchIcon className="h-5 w-5" />,
  notifications: <BellIcon className="h-5 w-5" />,
  integrations: <NetworkIcon className="h-5 w-5" />,
  system: <ServerIcon className="h-5 w-5" />,
  data: <DatabaseIcon className="h-5 w-5" />,
  users: <UsersIcon className="h-5 w-5" />,
};

interface SettingsSidebarProps {
  categories: SettingsCategory[];
  activeCategory: string;
  onCategoryChange: (categoryId: string) => void;
  matchingCategoryIds?: string[];
  isFiltering?: boolean;
  searchQuery?: string;
}

export const SettingsSidebar: React.FC<SettingsSidebarProps> = ({
  categories,
  activeCategory,
  onCategoryChange,
  matchingCategoryIds,
  isFiltering = false,
  searchQuery,
}) => {
  const navRef = useRef<HTMLUListElement>(null);
  const itemRefs = useRef<Map<string, HTMLButtonElement>>(new Map());

  const visibleCategories = useMemo(() => {
    if (!isFiltering || !matchingCategoryIds) {
      return categories;
    }

    return categories.filter((category) => matchingCategoryIds.includes(category.id));
  }, [categories, isFiltering, matchingCategoryIds]);

  const handleKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLButtonElement>, index: number) => {
      let nextIndex: number | null = null;

      switch (event.key) {
        case 'ArrowDown':
          event.preventDefault();
          nextIndex = index < visibleCategories.length - 1 ? index + 1 : 0;
          break;
        case 'ArrowUp':
          event.preventDefault();
          nextIndex = index > 0 ? index - 1 : visibleCategories.length - 1;
          break;
        case 'Home':
          event.preventDefault();
          nextIndex = 0;
          break;
        case 'End':
          event.preventDefault();
          nextIndex = visibleCategories.length - 1;
          break;
      }

      if (nextIndex !== null) {
        const nextCategory = visibleCategories[nextIndex];
        itemRefs.current.get(nextCategory.id)?.focus();
      }
    },
    [visibleCategories],
  );

  const setItemRef = useCallback((id: string, element: HTMLButtonElement | null) => {
    if (element) {
      itemRefs.current.set(id, element);
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
    [isFiltering, matchingCategoryIds],
  );

  return (
    <>
      <nav
        className="hidden w-64 shrink-0 self-start md:sticky md:top-4 md:block"
        aria-label="Settings categories"
      >
        <div className="rounded-r-3xl border-r border-pf-border/70 bg-pf-bg-0/70 px-3 py-3 backdrop-blur-sm">
          <ul ref={navRef} role="list" className="space-y-1">
            {visibleCategories.map((category, index) => {
              const isActive = activeCategory === category.id;
              const isMatching = isMatchingCategory(category.id);

              return (
                <li key={category.id}>
                  <button
                    ref={(element) => setItemRef(category.id, element)}
                    type="button"
                    onClick={() => onCategoryChange(category.id)}
                    onKeyDown={(event) => handleKeyDown(event, index)}
                    aria-current={isActive ? 'page' : undefined}
                    className={clsx(
                      'group relative w-full overflow-hidden rounded-2xl text-left text-sm',
                      'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
                      isActive && 'bg-pf-accent-bg/12 text-pf-text-primary shadow-[inset_0_1px_0_rgba(255,255,255,0.04)]',
                      !isActive && 'text-pf-text-secondary hover:bg-pf-bg-1/80 hover:text-pf-text-primary',
                      isMatching && !isActive && 'bg-pf-bg-1/70 text-pf-text-primary',
                    )}
                  >
                    <span
                      aria-hidden="true"
                      className={clsx(
                        'absolute inset-y-2 left-0 w-[3px] rounded-r-full transition-opacity duration-150 motion-reduce:transition-none',
                        isActive ? 'bg-pf-accent opacity-100' : 'bg-pf-accent opacity-0 group-hover:opacity-45',
                      )}
                    />
                    <span className="flex items-center gap-3 px-4 py-3">
                      <span
                        className={clsx(
                          'shrink-0 transition-all duration-[120ms] ease-out motion-reduce:transition-none',
                          isActive ? 'text-pf-accent' : 'text-pf-text-secondary group-hover:text-pf-text-primary',
                          !isActive && 'group-hover:translate-x-[2px]',
                        )}
                        aria-hidden="true"
                      >
                        {CATEGORY_ICONS[category.id] ?? <GearIcon className="h-5 w-5" />}
                      </span>
                      <span className="min-w-0 flex-1 truncate font-medium tracking-[0.01em]">
                        <SettingsMatchText text={category.label} query={searchQuery} />
                      </span>
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        </div>
      </nav>

      <MobileCategorySelector
        categories={visibleCategories}
        activeCategory={activeCategory}
        onCategoryChange={onCategoryChange}
        matchingCategoryIds={matchingCategoryIds}
        isFiltering={isFiltering}
        searchQuery={searchQuery}
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
  searchQuery?: string;
}

const MobileCategorySelector: React.FC<MobileCategorySelectorProps> = ({
  categories,
  activeCategory,
  onCategoryChange,
  matchingCategoryIds,
  isFiltering = false,
  searchQuery,
}) => {
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const [isOpen, setIsOpen] = useState(false);

  const activeLabel = categories.find((category) => category.id === activeCategory)?.label ?? 'Select';

  useEffect(() => {
    if (!isOpen) {
      return undefined;
    }

    const handleClickOutside = (event: MouseEvent) => {
      if (dropdownRef.current && !dropdownRef.current.contains(event.target as Node)) {
        setIsOpen(false);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) {
      return undefined;
    }

    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
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
    <div ref={dropdownRef} className="relative mb-4 md:hidden">
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setIsOpen((current) => !current)}
        aria-expanded={isOpen}
        aria-controls="settings-category-menu"
        aria-label={`Settings category: ${activeLabel}`}
        className="flex w-full items-center justify-between gap-2 rounded-2xl border border-pf-border bg-pf-bg-1/80 px-4 py-3 text-sm font-medium text-pf-text-primary shadow-sm backdrop-blur-sm focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
      >
        <span className="flex min-w-0 items-center gap-3">
          <span className="text-pf-accent" aria-hidden="true">
            {CATEGORY_ICONS[activeCategory] ?? <GearIcon className="h-5 w-5" />}
          </span>
          <span className="truncate">{activeLabel}</span>
        </span>
        <ChevronDownIcon className={clsx('h-5 w-5 transition-transform', isOpen && 'rotate-180')} />
      </button>

      {isOpen && (
        <ul
          id="settings-category-menu"
          aria-label="Settings categories"
          className="absolute z-50 mt-2 max-h-72 w-full overflow-auto rounded-2xl border border-pf-border bg-pf-bg-0/95 p-1 shadow-lg backdrop-blur-md"
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
                    'flex w-full items-center gap-3 rounded-xl px-4 py-2.5 text-left text-sm',
                    'focus:outline-hidden focus-visible:bg-pf-bg-1 focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
                    isActive && 'bg-pf-accent-bg/12 font-medium text-pf-text-primary',
                    !isActive && 'text-pf-text-secondary hover:bg-pf-bg-1 hover:text-pf-text-primary',
                    isMatching && !isActive && 'bg-pf-bg-1/80 text-pf-text-primary',
                  )}
                >
                  <span className={clsx(isActive ? 'text-pf-accent' : 'text-pf-text-secondary')} aria-hidden="true">
                    {CATEGORY_ICONS[category.id] ?? <GearIcon className="h-5 w-5" />}
                  </span>
                  <span className="min-w-0 flex-1 truncate">
                    <SettingsMatchText text={category.label} query={searchQuery} />
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
};
