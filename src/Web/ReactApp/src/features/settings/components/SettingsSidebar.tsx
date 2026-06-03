/* eslint-disable local/pf-no-raw-html-controls */
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { ChevronDownIcon } from '@/common/components/icons/MdiIcons';
import { SettingsMatchText } from '@/features/settings/components/SettingsMatchText';
import { getSettingsCategoryIcon } from '@/features/settings/settings-navigation';
import type { SettingsCategory } from '@/features/settings/types';

const NAV_ITEM_STAGGER_MS = 18;
const PREMIUM_TRANSITION_MS = 280;
const PREMIUM_EASING = 'cubic-bezier(0.16, 1, 0.3, 1)';

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
        className="hidden h-full min-h-0 flex-col bg-pf-bg-0/72 backdrop-blur-xl md:flex"
        aria-label="Settings categories"
      >
        <div className="border-b border-pf-border/70 px-4 py-5">
          <h1 className="text-lg leading-none text-pf-text-primary">Settings</h1>
          <p className="mt-3 text-sm text-pf-text-secondary">
            Hardware, slicing, user access, and system administration.
          </p>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto px-3 py-4">
          <ul role="list" className="space-y-1.5">
            {visibleCategories.map((category, index) => {
              const Icon = getSettingsCategoryIcon(category.id);
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
                    style={{ animationDelay: `${index * NAV_ITEM_STAGGER_MS}ms` }}
                    className={clsx(
                      'group relative w-full rounded-[1.1rem] border border-transparent px-4 py-3 text-left text-sm',
                      'motion-safe:animate-[pf-settings-nav-item-in_280ms_cubic-bezier(0.16,1,0.3,1)_both]',
                      'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
                      'transition-[transform,background-color,color,box-shadow,border-color] motion-reduce:transition-none active:scale-[0.985]',
                      isActive
                        ? 'border-pf-accent/35 bg-pf-accent-bg/25 text-pf-text-primary shadow-[inset_0_1px_0_rgba(255,255,255,0.08),inset_0_0_0_1px_rgba(255,255,255,0.04)]'
                        : isMatching
                          ? 'bg-pf-bg-1/75 text-pf-text-primary hover:border-pf-border/80 hover:bg-pf-bg-1/80'
                          : 'text-pf-text-secondary hover:border-pf-border/80 hover:bg-pf-bg-1/80 hover:text-pf-text-primary',
                    )}
                  >
                    <span
                      aria-hidden="true"
                      className={clsx(
                        'absolute inset-y-3 left-0 w-[3px] rounded-r-full transition-opacity motion-reduce:transition-none',
                        isActive ? 'bg-pf-accent opacity-100' : 'bg-pf-accent opacity-0 group-hover:opacity-55',
                      )}
                      style={{
                        transitionDuration: `${PREMIUM_TRANSITION_MS}ms`,
                        transitionTimingFunction: PREMIUM_EASING,
                      }}
                    />
                    <span className="flex items-center gap-3">
                      <span
                        className={clsx(
                          'shrink-0 transition-colors motion-reduce:transition-none',
                          isActive ? 'text-pf-accent' : 'text-pf-text-secondary group-hover:text-pf-text-primary',
                        )}
                        style={{
                          transitionDuration: `${PREMIUM_TRANSITION_MS}ms`,
                          transitionTimingFunction: PREMIUM_EASING,
                        }}
                        aria-hidden="true"
                      >
                        <Icon className="h-5 w-5" />
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

  const transitionStyle = {
    transitionDuration: `${PREMIUM_TRANSITION_MS}ms`,
    transitionTimingFunction: PREMIUM_EASING,
  } as const;

  return (
    <div ref={dropdownRef} className="relative border-b border-pf-border/70 px-4 py-4 md:hidden">
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setIsOpen((current) => !current)}
        aria-expanded={isOpen}
        aria-controls="settings-category-menu"
        aria-label={`Settings category: ${activeLabel}`}
        className="flex w-full items-center justify-between gap-2 rounded-2xl border border-pf-border/80 bg-pf-bg-1/85 px-4 py-3 text-sm font-medium text-pf-text-primary shadow-[inset_0_1px_0_rgba(255,255,255,0.05)] backdrop-blur-sm focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
      >
        <span className="flex min-w-0 items-center gap-3">
          <span className="text-pf-accent" aria-hidden="true">
            {React.createElement(getSettingsCategoryIcon(activeCategory), { className: 'h-5 w-5' })}
          </span>
          <span className="truncate">{activeLabel}</span>
        </span>
        <ChevronDownIcon className={clsx('h-5 w-5', isOpen && 'rotate-180')} style={transitionStyle} />
      </button>

      {isOpen && (
        <ul
          id="settings-category-menu"
          aria-label="Settings categories"
          className="absolute inset-x-4 top-[calc(100%-0.25rem)] z-50 max-h-72 overflow-auto rounded-2xl border border-pf-border bg-pf-bg-0/95 p-1 shadow-lg backdrop-blur-md"
        >
          {categories.map((category, index) => {
            const Icon = getSettingsCategoryIcon(category.id);
            const isActive = activeCategory === category.id;
            const isMatching = isMatchingCategory(category.id);

            return (
              <li key={category.id}>
                <button
                  type="button"
                  onClick={() => handleSelect(category.id)}
                  aria-current={isActive ? 'page' : undefined}
                  style={{ animationDelay: `${index * NAV_ITEM_STAGGER_MS}ms` }}
                  className={clsx(
                    'flex w-full items-center gap-3 rounded-xl border border-transparent px-4 py-2.5 text-left text-sm',
                    'motion-safe:animate-[pf-settings-nav-item-in_280ms_cubic-bezier(0.16,1,0.3,1)_both]',
                    'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
                    'transition-[transform,background-color,color,box-shadow,border-color] motion-reduce:transition-none active:scale-[0.985]',
                    isActive
                      ? 'border-pf-accent/35 bg-pf-accent-bg/25 font-medium text-pf-text-primary shadow-[inset_0_1px_0_rgba(255,255,255,0.08),inset_0_0_0_1px_rgba(255,255,255,0.04)]'
                      : isMatching
                        ? 'bg-pf-bg-1/75 text-pf-text-primary hover:border-pf-border/80 hover:bg-pf-bg-1/80'
                        : 'text-pf-text-secondary hover:border-pf-border/80 hover:bg-pf-bg-1/80 hover:text-pf-text-primary',
                  )}
                >
                  <span className={clsx(isActive ? 'text-pf-accent' : 'text-pf-text-secondary')} aria-hidden="true">
                    <Icon className="h-5 w-5" />
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
