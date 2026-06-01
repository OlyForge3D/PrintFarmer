/* eslint-disable local/pf-no-raw-html-controls */
import { useCallback, useRef } from 'react';
import clsx from 'clsx';
import type { SettingsSubPage } from '@/features/settings/types';

interface SettingsSubTabsProps {
  /** Sub-pages for the current category */
  subPages: SettingsSubPage[];
  /** Currently active sub-page ID */
  activeSubPage: string;
  /** Callback when sub-page changes */
  onSubPageChange: (subPageId: string) => void;
  /** IDs of sub-pages matching current search query */
  matchingSubPageIds?: string[];
  /** Whether a search filter is active */
  isFiltering?: boolean;
  /** Accessible label for the tab list */
  ariaLabel?: string;
}

export const SettingsSubTabs: React.FC<SettingsSubTabsProps> = ({
  subPages,
  activeSubPage,
  onSubPageChange,
  matchingSubPageIds,
  isFiltering = false,
  ariaLabel = 'Settings sub-pages',
}) => {
  const tabRefs = useRef<Map<string, HTMLButtonElement>>(new Map());

  // Store ref for each tab
  const setTabRef = useCallback((id: string, el: HTMLButtonElement | null) => {
    if (el) {
      tabRefs.current.set(id, el);
    } else {
      tabRefs.current.delete(id);
    }
  }, []);

  // Handle keyboard navigation within tabs
  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLButtonElement>, index: number) => {
      let nextIndex: number | null = null;

      switch (e.key) {
        case 'ArrowRight':
          e.preventDefault();
          nextIndex = index < subPages.length - 1 ? index + 1 : 0;
          break;
        case 'ArrowLeft':
          e.preventDefault();
          nextIndex = index > 0 ? index - 1 : subPages.length - 1;
          break;
        case 'Home':
          e.preventDefault();
          nextIndex = 0;
          break;
        case 'End':
          e.preventDefault();
          nextIndex = subPages.length - 1;
          break;
      }

      if (nextIndex !== null) {
        const nextSubPage = subPages[nextIndex];
        const nextTab = tabRefs.current.get(nextSubPage.id);
        nextTab?.focus();
      }
    },
    [subPages]
  );

  // Determine if a sub-page should be dimmed (search active but not matching)
  const isDimmed = useCallback(
    (subPageId: string) => {
      if (!isFiltering || !matchingSubPageIds) return false;
      return !matchingSubPageIds.includes(subPageId);
    },
    [isFiltering, matchingSubPageIds]
  );

  // Don't render if only one or zero sub-pages
  if (subPages.length < 2) {
    return null;
  }

  return (
    <div
      role="tablist"
      aria-label={ariaLabel}
      className="flex items-center gap-1 border-b border-pf-border mb-4 overflow-x-auto"
    >
      {subPages.map((subPage, index) => {
        const isActive = activeSubPage === subPage.id;
        const dimmed = isDimmed(subPage.id);

        return (
          <button
            key={subPage.id}
            ref={(el) => setTabRef(subPage.id, el)}
            type="button"
            role="tab"
            aria-selected={isActive}
            aria-controls={`panel-${subPage.id}`}
            id={`tab-${subPage.id}`}
            tabIndex={isActive ? 0 : -1}
            onClick={() => onSubPageChange(subPage.id)}
            onKeyDown={(e) => handleKeyDown(e, index)}
            className={clsx(
              'px-4 py-2 text-sm font-medium whitespace-nowrap transition-colors',
              'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
              isActive && 'text-pf-text-primary border-b-2 border-pf-accent -mb-px',
              !isActive && 'text-pf-text-secondary hover:text-pf-text-primary',
              dimmed && 'opacity-40'
            )}
          >
            {subPage.label}
          </button>
        );
      })}
    </div>
  );
};

export default SettingsSubTabs;
