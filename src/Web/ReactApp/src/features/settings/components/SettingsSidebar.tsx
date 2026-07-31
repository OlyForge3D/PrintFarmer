/* eslint-disable local/pf-no-raw-html-controls */
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { ChevronDownIcon } from '@/common/components/icons/MdiIcons';
import { SettingsMatchText } from '@/features/settings/components/SettingsMatchText';
import { getSettingsCategoryIcon } from '@/features/settings/settings-navigation';
import type { SettingsCategory, SettingsScope, SettingsScopeId } from '@/features/settings/types';

interface SettingsSidebarProps {
  /** Every category the current user can reach, across all available scopes. */
  categories: SettingsCategory[];
  activeScope: SettingsScopeId;
  activeCategory: string;
  availableScopes: SettingsScope[];
  onCategoryChange: (categoryId: string) => void;
  matchingCategoryIds?: string[];
  isFiltering?: boolean;
  searchQuery?: string;
}

interface NavGroup {
  scope: SettingsScope;
  categories: SettingsCategory[];
}

/** Short caption for a scope. The nav is the context, so "Settings" is redundant. */
const SCOPE_CAPTIONS: Record<SettingsScopeId, string> = {
  user: 'User',
  system: 'System',
  admin: 'Admin',
};

/**
 * Group categories under their scope, in the order the scopes are offered.
 *
 * Scope is a property of a category, not a mode the user has to enter first —
 * `resolveSettingsNavigationTarget` already derives the scope from whichever
 * category is picked. So the nav shows every destination at once and lets one
 * click do what used to take two.
 */
function buildNavGroups(categories: SettingsCategory[], scopes: SettingsScope[]): NavGroup[] {
  return scopes
    .map((scope) => ({
      scope,
      categories: categories.filter((category) => category.scopeId === scope.id),
    }))
    .filter((group) => group.categories.length > 0);
}

/**
 * Class list for one nav item.
 *
 * The active state is the Control Center's own tile treatment — raised surface,
 * hairline border, accent icon — so a settings category and an admin subsystem
 * read as the same kind of thing.
 */
function navItemClass(isActive: boolean, isMatching: boolean): string {
  return clsx(
    'group flex w-full items-center gap-3 rounded-md border px-3 py-2 text-left text-sm transition-colors',
    'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
    isActive
      ? 'border-pf-border bg-pf-bg-2 font-medium text-pf-text-primary'
      : isMatching
        ? 'border-transparent bg-pf-bg-1 text-pf-text-primary hover:border-pf-border'
        : 'border-transparent text-pf-text-secondary hover:bg-pf-bg-1 hover:text-pf-text-primary',
  );
}

export const SettingsSidebar: React.FC<SettingsSidebarProps> = ({
  categories,
  activeScope,
  activeCategory,
  availableScopes,
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

  const navGroups = useMemo(
    () => buildNavGroups(visibleCategories, availableScopes),
    [availableScopes, visibleCategories],
  );

  // With one group the caption states the obvious; the page title already says it.
  const showCaptions = navGroups.length > 1;

  const activeScopeMeta = useMemo(
    () => availableScopes.find((scope) => scope.id === activeScope) ?? availableScopes[0],
    [activeScope, availableScopes],
  );

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

  // Arrow keys walk the visible list as one sequence, across group boundaries.
  const flatIndexOf = useCallback(
    (categoryId: string) => visibleCategories.findIndex((category) => category.id === categoryId),
    [visibleCategories],
  );

  return (
    <>
      <nav
        className="hidden h-full min-h-0 flex-col bg-pf-sidebar md:flex"
        aria-label={`${activeScopeMeta?.label ?? 'Settings'} categories`}
      >
        <div className="min-h-0 flex-1 overflow-y-auto px-3 py-4">
          <div className="flex flex-col gap-5">
            {navGroups.map((group) => (
              <div key={group.scope.id}>
                {showCaptions && (
                  <h2
                    id={`settings-nav-group-${group.scope.id}`}
                    className="px-3 pb-2 text-xs font-semibold uppercase tracking-wide text-pf-text-tertiary"
                  >
                    {SCOPE_CAPTIONS[group.scope.id]}
                  </h2>
                )}
                <ul
                  role="list"
                  className="flex flex-col gap-1"
                  aria-labelledby={showCaptions ? `settings-nav-group-${group.scope.id}` : undefined}
                >
                  {group.categories.map((category) => {
                    const Icon = getSettingsCategoryIcon(category.id);
                    const isActive = activeCategory === category.id;

                    return (
                      <li key={category.id}>
                        <button
                          ref={(element) => setItemRef(category.id, element)}
                          type="button"
                          onClick={() => onCategoryChange(category.id)}
                          onKeyDown={(event) => handleKeyDown(event, flatIndexOf(category.id))}
                          aria-current={isActive ? 'page' : undefined}
                          className={navItemClass(isActive, isMatchingCategory(category.id))}
                        >
                          <span
                            className={clsx(
                              'shrink-0',
                              isActive
                                ? 'text-pf-accent'
                                : 'text-pf-text-tertiary group-hover:text-pf-text-secondary',
                            )}
                            aria-hidden="true"
                          >
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
              </div>
            ))}
          </div>
        </div>
      </nav>

      <MobileCategorySelector
        categories={visibleCategories}
        availableScopes={availableScopes}
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
  availableScopes: SettingsScope[];
  activeCategory: string;
  onCategoryChange: (categoryId: string) => void;
  matchingCategoryIds?: string[];
  isFiltering?: boolean;
  searchQuery?: string;
}

const MobileCategorySelector: React.FC<MobileCategorySelectorProps> = ({
  categories,
  availableScopes,
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
  const navGroups = useMemo(
    () => buildNavGroups(categories, availableScopes),
    [availableScopes, categories],
  );
  const showCaptions = navGroups.length > 1;

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
    <div ref={dropdownRef} className="relative border-b border-pf-border px-4 py-4 md:hidden">
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setIsOpen((current) => !current)}
        aria-controls="settings-category-menu"
        aria-expanded={isOpen}
        aria-label={`Settings category: ${activeLabel}`}
        title={`Settings category: ${activeLabel}`}
        className="flex w-full items-center justify-between gap-2 rounded-md border border-pf-border bg-pf-bg-1 px-4 py-3 text-sm font-medium text-pf-text-primary focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
      >
        <span className="flex min-w-0 items-center gap-3">
          <span className="text-pf-accent" aria-hidden="true">
            {React.createElement(getSettingsCategoryIcon(activeCategory), { className: 'h-5 w-5' })}
          </span>
          <span className="truncate">{activeLabel}</span>
        </span>
        <ChevronDownIcon className={clsx('h-5 w-5 transition-transform', isOpen && 'rotate-180')} />
      </button>

      {isOpen ? (
        <div
          id="settings-category-menu"
          className="absolute inset-x-4 top-[calc(100%-0.25rem)] z-50 max-h-72 overflow-auto rounded-md border border-pf-border bg-pf-panel p-2 shadow-lg"
        >
          <div className="flex flex-col gap-4">
            {navGroups.map((group) => (
              <div key={group.scope.id}>
                {showCaptions && (
                  <h2
                    id={`settings-mobile-nav-group-${group.scope.id}`}
                    className="px-2 pb-1.5 text-xs font-semibold uppercase tracking-wide text-pf-text-tertiary"
                  >
                    {SCOPE_CAPTIONS[group.scope.id]}
                  </h2>
                )}
                <ul
                  role="list"
                  className="flex flex-col gap-1"
                  aria-labelledby={
                    showCaptions ? `settings-mobile-nav-group-${group.scope.id}` : undefined
                  }
                >
                  {group.categories.map((category) => {
                    const Icon = getSettingsCategoryIcon(category.id);
                    const isActive = activeCategory === category.id;

                    return (
                      <li key={category.id}>
                        <button
                          type="button"
                          onClick={() => handleSelect(category.id)}
                          aria-current={isActive ? 'page' : undefined}
                          title={category.label}
                          className={navItemClass(isActive, isMatchingCategory(category.id))}
                        >
                          <span
                            className={clsx(
                              'shrink-0',
                              isActive ? 'text-pf-accent' : 'text-pf-text-tertiary',
                            )}
                            aria-hidden="true"
                          >
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
              </div>
            ))}
          </div>
        </div>
      ) : null}
    </div>
  );
};
