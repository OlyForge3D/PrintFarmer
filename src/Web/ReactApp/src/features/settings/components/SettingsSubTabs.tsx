/* eslint-disable local/pf-no-raw-html-controls */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { SettingsMatchText } from '@/features/settings/components/SettingsMatchText';
import type { SettingsSubPage } from '@/features/settings/types';

interface SettingsSubTabsProps {
  subPages: SettingsSubPage[];
  activeSubPage: string;
  onSubPageChange: (subPageId: string) => void;
  matchingSubPageIds?: string[];
  isFiltering?: boolean;
  ariaLabel?: string;
  searchQuery?: string;
}

interface TabIndicatorStyle {
  width: number;
  left: number;
}

export const SettingsSubTabs: React.FC<SettingsSubTabsProps> = ({
  subPages,
  activeSubPage,
  onSubPageChange,
  matchingSubPageIds,
  isFiltering = false,
  ariaLabel = 'Settings sub-pages',
  searchQuery,
}) => {
  const tabListRef = useRef<HTMLDivElement>(null);
  const tabRefs = useRef<Map<string, HTMLButtonElement>>(new Map());
  const [indicatorStyle, setIndicatorStyle] = useState<TabIndicatorStyle | null>(null);

  const visibleSubPages = useMemo(() => {
    if (!isFiltering || !matchingSubPageIds) {
      return subPages;
    }

    return subPages.filter((subPage) => matchingSubPageIds.includes(subPage.id));
  }, [isFiltering, matchingSubPageIds, subPages]);

  const setTabRef = useCallback((id: string, element: HTMLButtonElement | null) => {
    if (element) {
      tabRefs.current.set(id, element);
    } else {
      tabRefs.current.delete(id);
    }
  }, []);

  const handleKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLButtonElement>, index: number) => {
      let nextIndex: number | null = null;

      switch (event.key) {
        case 'ArrowRight':
          event.preventDefault();
          nextIndex = index < visibleSubPages.length - 1 ? index + 1 : 0;
          break;
        case 'ArrowLeft':
          event.preventDefault();
          nextIndex = index > 0 ? index - 1 : visibleSubPages.length - 1;
          break;
        case 'Home':
          event.preventDefault();
          nextIndex = 0;
          break;
        case 'End':
          event.preventDefault();
          nextIndex = visibleSubPages.length - 1;
          break;
      }

      if (nextIndex !== null) {
        tabRefs.current.get(visibleSubPages[nextIndex].id)?.focus();
      }
    },
    [visibleSubPages],
  );

  const isMatchingSubPage = useCallback(
    (subPageId: string) => {
      if (!isFiltering || !matchingSubPageIds) {
        return false;
      }

      return matchingSubPageIds.includes(subPageId);
    },
    [isFiltering, matchingSubPageIds],
  );

  useEffect(() => {
    const updateIndicator = () => {
      const activeTab = tabRefs.current.get(activeSubPage);
      const tabList = tabListRef.current;

      if (!activeTab || !tabList) {
        setIndicatorStyle(null);
        return;
      }

      setIndicatorStyle({
        width: activeTab.offsetWidth,
        left: activeTab.offsetLeft,
      });
    };

    updateIndicator();
    window.addEventListener('resize', updateIndicator);

    return () => {
      window.removeEventListener('resize', updateIndicator);
    };
  }, [activeSubPage, visibleSubPages]);

  if (visibleSubPages.length === 0 || (!isFiltering && visibleSubPages.length < 2)) {
    return null;
  }

  return (
    <div className="relative mb-5 overflow-x-auto border-b border-pf-border/70">
      <div ref={tabListRef} role="tablist" aria-label={ariaLabel} className="relative flex min-w-max items-center gap-1">
        {visibleSubPages.map((subPage, index) => {
          const isActive = activeSubPage === subPage.id;
          const isMatching = isMatchingSubPage(subPage.id);

          return (
            <button
              key={subPage.id}
              ref={(element) => setTabRef(subPage.id, element)}
              type="button"
              role="tab"
              aria-selected={isActive}
              aria-controls={`panel-${subPage.id}`}
              id={`tab-${subPage.id}`}
              tabIndex={isActive ? 0 : -1}
              onClick={() => onSubPageChange(subPage.id)}
              onKeyDown={(event) => handleKeyDown(event, index)}
              className={clsx(
                'relative inline-flex items-center px-4 py-3 text-sm font-medium whitespace-nowrap transition-colors duration-150 motion-reduce:transition-none',
                'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
                isActive && 'text-pf-text-primary',
                !isActive && 'text-pf-text-secondary hover:text-pf-text-primary',
                isMatching && !isActive && 'rounded-t-xl bg-pf-bg-1/85 text-pf-text-primary',
              )}
            >
              <SettingsMatchText text={subPage.label} query={searchQuery} />
            </button>
          );
        })}

        {indicatorStyle ? (
          <span
            aria-hidden="true"
            className="absolute bottom-0 h-[2px] rounded-full bg-pf-accent transition-[transform,width] duration-150 ease-out motion-reduce:transition-none"
            style={{
              width: `${indicatorStyle.width}px`,
              transform: `translateX(${indicatorStyle.left}px)`,
            }}
          />
        ) : null}
      </div>
    </div>
  );
};

export default SettingsSubTabs;
