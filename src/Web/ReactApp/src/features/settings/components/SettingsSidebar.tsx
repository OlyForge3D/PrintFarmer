/* eslint-disable local/pf-no-raw-html-controls */
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { ChevronDownIcon } from '@/common/components/icons/MdiIcons';
import { SettingsMatchText } from '@/features/settings/components/SettingsMatchText';
import { getSettingsCategoryIcon } from '@/features/settings/settings-navigation';
import type { SettingsCategory, SettingsScope, SettingsScopeId } from '@/features/settings/types';

interface SettingsSidebarProps {
  categories: SettingsCategory[];
  activeScope: SettingsScopeId;
  activeCategory: string;
  availableScopes: SettingsScope[];
  onCategoryChange: (categoryId: string) => void;
  onScopeChange: (scopeId: SettingsScopeId) => void;
  matchingCategoryIds?: string[];
  isFiltering?: boolean;
  searchQuery?: string;
}

export const SettingsSidebar: React.FC<SettingsSidebarProps> = ({
  categories,
  activeScope,
  activeCategory,
  availableScopes,
  onCategoryChange,
  onScopeChange,
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

  const primaryScopes = useMemo(
    () => availableScopes.filter((scope) => scope.id !== 'admin'),
    [availableScopes],
  );
  const adminScope = useMemo(
    () => availableScopes.find((scope) => scope.id === 'admin'),
    [availableScopes],
  );
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

  return (
    <>
      <nav
        className="hidden h-full min-h-0 flex-col bg-pf-bg-0/72 backdrop-blur-xl md:flex"
        aria-label={`${activeScopeMeta?.label ?? 'Settings'} categories`}
      >
        <div className="border-b border-pf-border/70 px-4 py-5">
          {primaryScopes.length > 1 ? (
            <ScopeSwitcher
              scopes={primaryScopes}
              activeScope={activeScope}
              onScopeChange={onScopeChange}
            />
          ) : null}

          {adminScope ? (
            <button
              type="button"
              onClick={() => onScopeChange(adminScope.id)}
              aria-pressed={activeScope === adminScope.id ? 'true' : 'false'}
              title={adminScope.label}
              className={clsx(
                'mt-3 flex w-full items-center justify-start rounded-2xl border px-3 py-2 text-left text-sm transition-colors',
                activeScope === adminScope.id
                  ? 'border-pf-accent/35 bg-pf-accent-bg/25 text-pf-text-primary'
                  : 'border-pf-border/80 bg-pf-bg-1/75 text-pf-text-secondary hover:border-pf-border hover:bg-pf-bg-1/90 hover:text-pf-text-primary',
              )}
            >
              <span className="font-medium">{adminScope.label}</span>
            </button>
          ) : null}
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
                    className={clsx(
                      'group relative w-full rounded-[1.1rem] px-4 py-3 text-left text-sm',
                      'motion-safe:animate-[pf-settings-nav-item-in_280ms_cubic-bezier(0.16,1,0.3,1)_both]',
                      'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
                      'transition-[transform,background-color,color,box-shadow] motion-reduce:transition-none active:scale-[0.985]',
                      isActive
                        ? 'bg-pf-accent-bg/25 font-medium text-pf-text-primary shadow-[inset_0_1px_0_rgba(255,255,255,0.08),inset_0_0_0_1px_rgba(255,255,255,0.04)]'
                        : isMatching
                          ? 'bg-pf-bg-1/75 text-pf-text-primary hover:bg-pf-bg-1/80'
                          : 'text-pf-text-secondary hover:bg-pf-bg-1/80 hover:text-pf-text-primary',
                    )}
                  >
                    <span className="flex items-center gap-3">
                      <span
                        className={clsx(
                          'shrink-0 transition-colors motion-reduce:transition-none',
                          isActive ? 'text-pf-accent' : 'text-pf-text-secondary group-hover:text-pf-text-primary',
                        )}
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
        activeScope={activeScope}
        activeCategory={activeCategory}
        availableScopes={availableScopes}
        onCategoryChange={onCategoryChange}
        onScopeChange={onScopeChange}
        matchingCategoryIds={matchingCategoryIds}
        isFiltering={isFiltering}
        searchQuery={searchQuery}
      />
    </>
  );
};

interface ScopeSwitcherProps {
  scopes: SettingsScope[];
  activeScope: SettingsScopeId;
  onScopeChange: (scopeId: SettingsScopeId) => void;
  className?: string;
}

function ScopeSwitcher({ scopes, activeScope, onScopeChange, className }: ScopeSwitcherProps) {
  return (
    <div className={clsx('rounded-2xl border border-pf-border/80 bg-pf-bg-1/75 p-1', className)} role="radiogroup" aria-label="Settings scopes">
      <div className="grid grid-cols-2 gap-1">
        {scopes.map((scope) => {
          const isActive = scope.id === activeScope;
          return (
            <button
              key={scope.id}
              type="button"
              role="radio"
              aria-checked={isActive}
              title={scope.id === 'user' ? 'User' : scope.id === 'system' ? 'System' : scope.label}
              onClick={() => onScopeChange(scope.id)}
              className={clsx(
                'rounded-xl px-3 py-2 text-sm font-medium transition-colors focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
                isActive
                  ? 'bg-pf-accent-bg/30 text-pf-text-primary shadow-[inset_0_0_0_1px_rgba(255,255,255,0.05)]'
                  : 'text-pf-text-secondary hover:bg-pf-bg-0/85 hover:text-pf-text-primary',
              )}
            >
              {scope.id === 'user' ? 'User' : scope.id === 'system' ? 'System' : scope.label}
            </button>
          );
        })}
      </div>
    </div>
  );
}

interface MobileCategorySelectorProps {
  categories: SettingsCategory[];
  activeScope: SettingsScopeId;
  activeCategory: string;
  availableScopes: SettingsScope[];
  onCategoryChange: (categoryId: string) => void;
  onScopeChange: (scopeId: SettingsScopeId) => void;
  matchingCategoryIds?: string[];
  isFiltering?: boolean;
  searchQuery?: string;
}

const MobileCategorySelector: React.FC<MobileCategorySelectorProps> = ({
  categories,
  activeScope,
  activeCategory,
  availableScopes,
  onCategoryChange,
  onScopeChange,
  matchingCategoryIds,
  isFiltering = false,
  searchQuery,
}) => {
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const [isOpen, setIsOpen] = useState(false);

  const activeLabel = categories.find((category) => category.id === activeCategory)?.label ?? 'Select';
  const primaryScopes = availableScopes.filter((scope) => scope.id !== 'admin');
  const adminScope = availableScopes.find((scope) => scope.id === 'admin');

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

  const handleScopeSelect = (scopeId: SettingsScopeId) => {
    onScopeChange(scopeId);
    setIsOpen(false);
  };

  const isMatchingCategory = (categoryId: string) => {
    if (!isFiltering || !matchingCategoryIds) {
      return false;
    }

    return matchingCategoryIds.includes(categoryId);
  };

  return (
    <div ref={dropdownRef} className="relative border-b border-pf-border/70 px-4 py-4 md:hidden">
      {primaryScopes.length > 1 ? (
        <ScopeSwitcher
          scopes={primaryScopes}
          activeScope={activeScope}
          onScopeChange={handleScopeSelect}
          className="mb-3"
        />
      ) : null}

      {adminScope ? (
        <button
          type="button"
          onClick={() => handleScopeSelect(adminScope.id)}
          aria-pressed={activeScope === adminScope.id}
          className={clsx(
            'mb-3 flex w-full items-center justify-start rounded-2xl border px-3 py-2 text-left text-sm transition-colors',
            activeScope === adminScope.id
              ? 'border-pf-accent/35 bg-pf-accent-bg/25 text-pf-text-primary'
              : 'border-pf-border/80 bg-pf-bg-1/75 text-pf-text-secondary hover:border-pf-border hover:bg-pf-bg-1/90 hover:text-pf-text-primary',
          )}
        >
          <span className="font-medium">{adminScope.label}</span>
        </button>
      ) : null}

      <button
        ref={buttonRef}
        type="button"
        onClick={() => setIsOpen((current) => !current)}
        aria-controls="settings-category-menu"
        aria-label={`Settings category: ${activeLabel}`}
          title={`Settings category: ${activeLabel}`}
        className="flex w-full items-center justify-between gap-2 rounded-2xl border border-pf-border/80 bg-pf-bg-1/85 px-4 py-3 text-sm font-medium text-pf-text-primary shadow-[inset_0_1px_0_rgba(255,255,255,0.05)] backdrop-blur-sm focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
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
        <ul
          id="settings-category-menu"
          aria-label="Settings categories"
          className="absolute inset-x-4 top-[calc(100%-0.25rem)] z-50 max-h-72 overflow-auto rounded-2xl border border-pf-border bg-pf-bg-0/95 p-1 shadow-lg backdrop-blur-md"
        >
          {categories.map((category) => {
            const Icon = getSettingsCategoryIcon(category.id);
            const isActive = activeCategory === category.id;
            const isMatching = isMatchingCategory(category.id);

            return (
              <li key={category.id}>
                <button
                  type="button"
                  onClick={() => handleSelect(category.id)}
                  aria-current={isActive ? 'page' : undefined}
                  title={category.label}
                  className={clsx(
                    'flex w-full items-center gap-3 rounded-xl px-4 py-2.5 text-left text-sm',
                    'motion-safe:animate-[pf-settings-nav-item-in_280ms_cubic-bezier(0.16,1,0.3,1)_both]',
                    'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
                    'transition-[transform,background-color,color,box-shadow] motion-reduce:transition-none active:scale-[0.985]',
                    isActive
                      ? 'bg-pf-accent-bg/25 font-medium text-pf-text-primary shadow-[inset_0_1px_0_rgba(255,255,255,0.08),inset_0_0_0_1px_rgba(255,255,255,0.04)]'
                      : isMatching
                        ? 'bg-pf-bg-1/75 text-pf-text-primary hover:bg-pf-bg-1/80'
                        : 'text-pf-text-secondary hover:bg-pf-bg-1/80 hover:text-pf-text-primary',
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
      ) : null}
    </div>
  );
};
