/* eslint-disable local/pf-no-raw-html-controls */
import { useCallback, useMemo, useRef } from 'react';
import clsx from 'clsx';
import { Badge } from '@/common/components/ui';
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

  const visibleSubPages = useMemo(() => {
    if (!isFiltering || !matchingSubPageIds) {
      return subPages;
    }

    return subPages.filter((subPage) => matchingSubPageIds.includes(subPage.id));
  }, [isFiltering, matchingSubPageIds, subPages]);

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
          nextIndex = index < visibleSubPages.length - 1 ? index + 1 : 0;
          break;
        case 'ArrowLeft':
          e.preventDefault();
          nextIndex = index > 0 ? index - 1 : visibleSubPages.length - 1;
          break;
        case 'Home':
          e.preventDefault();
          nextIndex = 0;
          break;
        case 'End':
          e.preventDefault();
          nextIndex = visibleSubPages.length - 1;
          break;
      }

      if (nextIndex !== null) {
        const nextSubPage = visibleSubPages[nextIndex];
        const nextTab = tabRefs.current.get(nextSubPage.id);
        nextTab?.focus();
      }
    },
    [visibleSubPages]
  );

  const isMatchingSubPage = useCallback(
    (subPageId: string) => {
      if (!isFiltering || !matchingSubPageIds) {
        return false;
      }

      return matchingSubPageIds.includes(subPageId);
    },
    [isFiltering, matchingSubPageIds]
  );

  // During filtering, keep a single matching result visible so search behaves like navigation.
  if (visibleSubPages.length === 0 || (!isFiltering && visibleSubPages.length < 2)) {
    return null;
  }

  return (
    <div
      role="tablist"
      aria-label={ariaLabel}
      className="flex items-center gap-1 border-b border-pf-border mb-4 overflow-x-auto"
    >
      {visibleSubPages.map((subPage, index) => {
        const isActive = activeSubPage === subPage.id;
        const isMatching = isMatchingSubPage(subPage.id);

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
              'inline-flex items-center gap-2 px-4 py-2 text-sm font-medium whitespace-nowrap transition-colors rounded-t-md',
              'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
              isActive && 'text-pf-text-primary border-b-2 border-pf-accent -mb-px',
              !isActive && 'text-pf-text-secondary hover:text-pf-text-primary',
              isMatching && !isActive && 'bg-pf-accent-bg text-[var(--pf-on-accent)]'
            )}
          >
            <span>{subPage.label}</span>
            {isMatching ? <Badge variant="info">Match</Badge> : null}
          </button>
        );
      })}
    </div>
  );
};

export default SettingsSubTabs;
