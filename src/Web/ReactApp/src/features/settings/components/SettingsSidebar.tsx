/* eslint-disable local/pf-no-raw-html-controls */
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { ChevronDownIcon, SettingsIcon } from '@/common/components/icons/MdiIcons';
import { SettingsMatchText } from '@/features/settings/components/SettingsMatchText';
import { getSettingsCategoryIcon, resolveSettingsNavigationTarget } from '@/features/settings/settings-navigation';
import type { SettingsCategory, SettingsScope, SettingsScopeId } from '@/features/settings/types';
import {
  getSettingsGroupedDestinations,
  type AdminDestination,
  type AdminDestinationAccess,
} from '@/features/admin/registry/adminDestinations';

interface SettingsSidebarProps {
  /** Every category the current user can reach, across all available scopes. */
  categories: SettingsCategory[];
  activeScope: SettingsScopeId;
  activeCategory: string;
  activeSubPage?: string;
  activeDestinationId?: string;
  availableScopes: SettingsScope[];
  onCategoryChange: (categoryId: string, subPageId?: string) => void;
  onSelectDestination?: (destination: AdminDestination) => void;
  destinationAccess?: AdminDestinationAccess;
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
/**
 * One rail entry, sized to the proposal's `.p-nav a`: 13.5px label, 16px icon,
 * 9px gap, 7px/10px padding. Shipped metrics were a 14px label on a 20px icon
 * with 12px gaps and 8px/12px padding, which read as a stack of buttons rather
 * than a list of places and pushed a ten-item rail past the fold.
 *
 * The active treatment is unchanged — a filled `bg-pf-bg-2` with a real border
 * against a transparent border on the rest — because it already matched.
 */
function navItemClass(isActive: boolean, isMatching: boolean): string {
  return clsx(
    'group flex w-full items-center gap-2.5 rounded-md border px-2.5 py-[7px] text-left text-[13.5px] transition-colors',
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
  activeSubPage,
  activeDestinationId,
  availableScopes,
  onCategoryChange,
  onSelectDestination,
  destinationAccess,
  matchingCategoryIds,
  isFiltering = false,
  searchQuery,
}) => {
  const itemRefs = useRef<Map<string, HTMLButtonElement>>(new Map());

  const access = useMemo(
    () => destinationAccess ?? { hasRole: () => true, isFarmAdmin: true },
    [destinationAccess],
  );

  // System scope: 8 display groups consuming adminDestinations.ts
  const systemGroups = useMemo(() => {
    const rawGroups = getSettingsGroupedDestinations(access);
    if (!isFiltering || !searchQuery) {
      return rawGroups;
    }

    const query = searchQuery.trim().toLowerCase();
    if (!query) return rawGroups;

    return rawGroups
      .map((g) => {
        const filtered = g.destinations.filter((dest) => {
          const matchLabel = dest.label.toLowerCase().includes(query);
          const matchDesc = dest.description?.toLowerCase().includes(query);
          const matchKey = dest.keywords?.some((k) => k.toLowerCase().includes(query));
          return matchLabel || matchDesc || matchKey;
        });
        return { ...g, destinations: filtered };
      })
      .filter((g) => g.destinations.length > 0);
  }, [access, isFiltering, searchQuery]);

  const flatSystemDestinations = useMemo(
    () => systemGroups.flatMap((g) => g.destinations),
    [systemGroups],
  );

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

  const isSystemScope = activeScope === 'system';

  // With one group the caption states the obvious; the page title already says it.
  const showCaptions = isSystemScope ? true : navGroups.length > 1;

  const activeScopeMeta = useMemo(
    () => availableScopes.find((scope) => scope.id === activeScope) ?? availableScopes[0],
    [activeScope, availableScopes],
  );

  const handleKeyDownUser = useCallback(
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

  const handleKeyDownSystem = useCallback(
    (event: React.KeyboardEvent<HTMLButtonElement>, index: number) => {
      let nextIndex: number | null = null;

      switch (event.key) {
        case 'ArrowDown':
          event.preventDefault();
          nextIndex = index < flatSystemDestinations.length - 1 ? index + 1 : 0;
          break;
        case 'ArrowUp':
          event.preventDefault();
          nextIndex = index > 0 ? index - 1 : flatSystemDestinations.length - 1;
          break;
        case 'Home':
          event.preventDefault();
          nextIndex = 0;
          break;
        case 'End':
          event.preventDefault();
          nextIndex = flatSystemDestinations.length - 1;
          break;
      }

      if (nextIndex !== null) {
        const nextDest = flatSystemDestinations[nextIndex];
        itemRefs.current.get(nextDest.id)?.focus();
      }
    },
    [flatSystemDestinations],
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

  const isMatchingDestination = useCallback(
    (dest: AdminDestination) => {
      if (!isFiltering || !searchQuery) return false;
      const q = searchQuery.trim().toLowerCase();
      if (!q) return false;
      return (
        dest.label.toLowerCase().includes(q) ||
        (dest.description?.toLowerCase().includes(q) ?? false) ||
        (dest.keywords?.some((k) => k.toLowerCase().includes(q)) ?? false)
      );
    },
    [isFiltering, searchQuery],
  );

  // Arrow keys walk the visible list as one sequence, across group boundaries.
  const flatIndexOfUser = useCallback(
    (categoryId: string) => visibleCategories.findIndex((category) => category.id === categoryId),
    [visibleCategories],
  );

  const flatIndexOfSystem = useCallback(
    (destId: string) => flatSystemDestinations.findIndex((dest) => dest.id === destId),
    [flatSystemDestinations],
  );

  const isDestActive = useCallback(
    (dest: AdminDestination) => {
      if (activeDestinationId) {
        return activeDestinationId === dest.id;
      }
      if (activeCategory === dest.id) {
        return true;
      }
      if (dest.path.includes('?')) {
        const queryParams = new URLSearchParams(dest.path.split('?')[1]);
        const destTab = queryParams.get('tab');
        const destSub = queryParams.get('sub');
        if (destTab) {
          const target = resolveSettingsNavigationTarget(destTab, destSub ?? undefined, 'system');
          if (target.categoryId === activeCategory) {
            if (destSub) {
              return destSub === activeSubPage;
            }
            return !activeSubPage || activeSubPage === '' || target.subPageId === activeSubPage;
          }
        }
      }
      return false;
    },
    [activeCategory, activeDestinationId, activeSubPage],
  );

  const handleDestinationClick = useCallback(
    (dest: AdminDestination) => {
      if (onSelectDestination) {
        onSelectDestination(dest);
      } else if (dest.path.includes('?')) {
        const queryParams = new URLSearchParams(dest.path.split('?')[1]);
        const destTab = queryParams.get('tab');
        const destSub = queryParams.get('sub');
        if (destTab) {
          onCategoryChange(destTab, destSub ?? undefined);
        } else {
          onCategoryChange(dest.id);
        }
      } else {
        onCategoryChange(dest.id);
      }
    },
    [onCategoryChange, onSelectDestination],
  );

  return (
    <>
      <nav
        className="hidden h-fit min-h-0 flex-col self-start rounded-md border border-pf-border bg-pf-sidebar md:flex"
        aria-label={`${activeScopeMeta?.label ?? 'Settings'} categories`}
      >
        <div className="min-h-0 flex-1 overflow-y-auto p-2">
          <div className="flex flex-col gap-5">
            {isSystemScope ? (
              systemGroups.map((g) => (
                <div key={g.group.id}>
                  <h2
                    id={`settings-nav-group-${g.group.id}`}
                    className="px-2.5 pb-1.5 text-[10.5px] font-semibold uppercase tracking-[0.09em] text-pf-text-tertiary"
                  >
                    {g.group.label}
                  </h2>
                  <ul
                    role="list"
                    className="flex flex-col gap-1"
                    aria-labelledby={`settings-nav-group-${g.group.id}`}
                  >
                    {g.destinations.map((dest) => {
                      const Icon = dest.icon;
                      const isActive = isDestActive(dest);

                      return (
                        <li key={dest.id}>
                          <button
                            ref={(element) => setItemRef(dest.id, element)}
                            type="button"
                            onClick={() => handleDestinationClick(dest)}
                            onKeyDown={(event) =>
                              handleKeyDownSystem(event, flatIndexOfSystem(dest.id))
                            }
                            aria-current={isActive ? 'page' : undefined}
                            className={navItemClass(isActive, isMatchingDestination(dest))}
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
                              <Icon className="h-4 w-4" />
                            </span>
                            <span className="min-w-0 flex-1 truncate">
                              <SettingsMatchText text={dest.label} query={searchQuery} />
                            </span>
                          </button>
                        </li>
                      );
                    })}
                  </ul>
                </div>
              ))
            ) : (
              navGroups.map((group) => (
                <div key={group.scope.id}>
                  {showCaptions && (
                    <h2
                      id={`settings-nav-group-${group.scope.id}`}
                      className="px-2.5 pb-1.5 text-[10.5px] font-semibold uppercase tracking-[0.09em] text-pf-text-tertiary"
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
                            onKeyDown={(event) => handleKeyDownUser(event, flatIndexOfUser(category.id))}
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
                              <Icon className="h-4 w-4" />
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
              ))
            )}
          </div>
        </div>
      </nav>

      <MobileCategorySelector
        categories={visibleCategories}
        activeScope={activeScope}
        systemGroups={systemGroups}
        availableScopes={availableScopes}
        activeCategory={activeCategory}
        activeSubPage={activeSubPage}
        activeDestinationId={activeDestinationId}
        onCategoryChange={onCategoryChange}
        onSelectDestination={onSelectDestination}
        matchingCategoryIds={matchingCategoryIds}
        isFiltering={isFiltering}
        searchQuery={searchQuery}
      />
    </>
  );
};

interface MobileCategorySelectorProps {
  categories: SettingsCategory[];
  activeScope: SettingsScopeId;
  systemGroups: Array<{ group: { id: string; label: string }; destinations: AdminDestination[] }>;
  availableScopes: SettingsScope[];
  activeCategory: string;
  activeSubPage?: string;
  activeDestinationId?: string;
  onCategoryChange: (categoryId: string, subPageId?: string) => void;
  onSelectDestination?: (destination: AdminDestination) => void;
  matchingCategoryIds?: string[];
  isFiltering?: boolean;
  searchQuery?: string;
}

const MobileCategorySelector: React.FC<MobileCategorySelectorProps> = ({
  categories,
  activeScope,
  systemGroups,
  availableScopes,
  activeCategory,
  activeSubPage,
  activeDestinationId,
  onCategoryChange,
  onSelectDestination,
  matchingCategoryIds,
  isFiltering = false,
  searchQuery,
}) => {
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const [isOpen, setIsOpen] = useState(false);

  const isSystemScope = activeScope === 'system';

  const flatSystemDestinations = useMemo(
    () => systemGroups.flatMap((g) => g.destinations),
    [systemGroups],
  );

  const activeDest = useMemo(() => {
    if (!isSystemScope) return null;
    if (activeDestinationId) {
      return flatSystemDestinations.find((d) => d.id === activeDestinationId) ?? null;
    }
    return flatSystemDestinations.find((d) => {
      if (d.id === activeCategory) return true;
      if (d.path.includes('?')) {
        const queryParams = new URLSearchParams(d.path.split('?')[1]);
        const destTab = queryParams.get('tab');
        const destSub = queryParams.get('sub');
        if (destTab) {
          if (destSub) {
            return destTab === activeCategory && destSub === activeSubPage;
          }
          return destTab === activeCategory && (!activeSubPage || activeSubPage === '');
        }
      }
      return false;
    }) ?? null;
  }, [activeCategory, activeDestinationId, activeSubPage, flatSystemDestinations, isSystemScope]);

  const activeLabel = isSystemScope
    ? (activeDest?.label ?? 'Select section')
    : (categories.find((category) => category.id === activeCategory)?.label ?? 'Select section');

  const ActiveIcon = isSystemScope
    ? (activeDest?.icon ?? SettingsIcon)
    : getSettingsCategoryIcon(activeCategory);

  const navGroups = useMemo(
    () => buildNavGroups(categories, availableScopes),
    [availableScopes, categories],
  );
  const showCaptions = isSystemScope ? true : navGroups.length > 1;

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

  const handleSelectCategory = (categoryId: string) => {
    onCategoryChange(categoryId);
    setIsOpen(false);
    buttonRef.current?.focus();
  };

  const handleSelectDestination = (dest: AdminDestination) => {
    if (onSelectDestination) {
      onSelectDestination(dest);
    } else {
      onCategoryChange(dest.tab || dest.id);
    }
    setIsOpen(false);
    buttonRef.current?.focus();
  };

  const isMatchingCategory = (categoryId: string) => {
    if (!isFiltering || !matchingCategoryIds) {
      return false;
    }

    return matchingCategoryIds.includes(categoryId);
  };

  const isMatchingDestination = (dest: AdminDestination) => {
    if (!isFiltering || !searchQuery) return false;
    const q = searchQuery.trim().toLowerCase();
    if (!q) return false;
    return (
      dest.label.toLowerCase().includes(q) ||
      (dest.description?.toLowerCase().includes(q) ?? false) ||
      (dest.keywords?.some((k) => k.toLowerCase().includes(q)) ?? false)
    );
  };

  const isDestActive = (dest: AdminDestination) => {
    if (activeDestinationId) {
      return activeDestinationId === dest.id;
    }
    return activeCategory === dest.id || activeCategory === dest.tab;
  };

  return (
    <div ref={dropdownRef} className="relative border-b border-pf-border px-4 py-4 md:hidden">
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setIsOpen((current) => !current)}
        aria-controls="settings-category-menu"
        aria-expanded={isOpen}
        aria-label={`Settings section: ${activeLabel}`}
        title={`Settings section: ${activeLabel}`}
        className="flex w-full items-center justify-between gap-2 rounded-md border border-pf-border bg-pf-bg-1 px-4 py-3 text-sm font-medium text-pf-text-primary focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
      >
        <span className="flex min-w-0 items-center gap-3">
          <span className="text-pf-accent" aria-hidden="true">
            {React.createElement(ActiveIcon, { className: 'h-5 w-5' })}
          </span>
          <span className="truncate">{activeLabel}</span>
        </span>
        <ChevronDownIcon className={clsx('h-5 w-5 transition-transform', isOpen && 'rotate-180')} />
      </button>

      {isOpen ? (
        <nav
          id="settings-category-menu"
          aria-label="Settings categories"
          className="absolute inset-x-4 top-[calc(100%-0.25rem)] z-50 max-h-72 overflow-auto rounded-md border border-pf-border bg-pf-panel p-2 shadow-lg"
        >
          <div className="flex flex-col gap-4">
            {isSystemScope ? (
              systemGroups.map((g) => (
                <div key={g.group.id}>
                  <h2
                    id={`settings-mobile-nav-group-${g.group.id}`}
                    className="px-2 pb-1.5 text-[10.5px] font-semibold uppercase tracking-[0.09em] text-pf-text-tertiary"
                  >
                    {g.group.label}
                  </h2>
                  <ul
                    role="list"
                    className="flex flex-col gap-1"
                    aria-labelledby={`settings-mobile-nav-group-${g.group.id}`}
                  >
                    {g.destinations.map((dest) => {
                      const Icon = dest.icon;
                      const isActive = isDestActive(dest);

                      return (
                        <li key={dest.id}>
                          <button
                            type="button"
                            onClick={() => handleSelectDestination(dest)}
                            aria-current={isActive ? 'page' : undefined}
                            title={dest.label}
                            className={navItemClass(isActive, isMatchingDestination(dest))}
                          >
                            <span
                              className={clsx(
                                'shrink-0',
                                isActive ? 'text-pf-accent' : 'text-pf-text-tertiary',
                              )}
                              aria-hidden="true"
                            >
                              <Icon className="h-4 w-4" />
                            </span>
                            <span className="min-w-0 flex-1 truncate">
                              <SettingsMatchText text={dest.label} query={searchQuery} />
                            </span>
                          </button>
                        </li>
                      );
                    })}
                  </ul>
                </div>
              ))
            ) : (
              navGroups.map((group) => (
                <div key={group.scope.id}>
                  {showCaptions && (
                    <h2
                      id={`settings-mobile-nav-group-${group.scope.id}`}
                      className="px-2 pb-1.5 text-[10.5px] font-semibold uppercase tracking-[0.09em] text-pf-text-tertiary"
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
                            onClick={() => handleSelectCategory(category.id)}
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
                              <Icon className="h-4 w-4" />
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
              ))
            )}
          </div>
        </nav>
      ) : null}
    </div>
  );
};
