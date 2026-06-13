import { useMemo } from 'react';
import { Tabs } from '@/common/components/ui/Tabs';
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

export const SettingsSubTabs: React.FC<SettingsSubTabsProps> = ({
  subPages,
  activeSubPage,
  onSubPageChange,
  matchingSubPageIds,
  isFiltering = false,
  ariaLabel = 'Settings sub-pages',
  searchQuery,
}) => {
  const visibleSubPages = useMemo(() => {
    if (!isFiltering || !matchingSubPageIds) {
      return subPages;
    }

    return subPages.filter((subPage) => matchingSubPageIds.includes(subPage.id));
  }, [isFiltering, matchingSubPageIds, subPages]);

  if (visibleSubPages.length === 0 || (!isFiltering && visibleSubPages.length < 2)) {
    return null;
  }

  return (
    <div className="relative mt-4">
      <Tabs activeTab={activeSubPage} onTabChange={onSubPageChange}>
        <Tabs.List
          className="border-b border-pf-border bg-transparent !p-0 px-4 overflow-x-auto"
          aria-label={ariaLabel}
        >
          {visibleSubPages.map((subPage) => {
            const isMatching = isFiltering && matchingSubPageIds?.includes(subPage.id) && activeSubPage !== subPage.id;
            return (
              <Tabs.Tab
                key={subPage.id}
                id={subPage.id}
                className={isMatching ? 'bg-pf-bg-1/85 !text-pf-text-primary' : ''}
              >
                <SettingsMatchText text={subPage.label} query={searchQuery} />
              </Tabs.Tab>
            );
          })}
        </Tabs.List>
      </Tabs>
    </div>
  );
};

export default SettingsSubTabs;
