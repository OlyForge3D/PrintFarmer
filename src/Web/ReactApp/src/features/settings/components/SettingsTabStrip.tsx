import { useEffect, useRef } from 'react';
import { Tabs } from '@/common/components/ui';
import { SETTINGS_TABS } from '@/features/settings/types';

interface SettingsTabStripProps {
  activeTab: string;
  onTabChange: (tabId: string) => void;
  filteredTabIds?: string[];
  highlight?: string;
}

export const SettingsTabStrip: React.FC<SettingsTabStripProps> = ({
  activeTab,
  onTabChange,
  filteredTabIds,
  highlight,
}) => {
  const visibleTabs = filteredTabIds
    ? SETTINGS_TABS.filter((t) => filteredTabIds.includes(t.id))
    : SETTINGS_TABS;

  const highlightRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (highlight && highlightRef.current) {
      highlightRef.current.scrollIntoView?.({ behavior: 'smooth', block: 'nearest' });
    }
  }, [highlight, activeTab]);

  return (
    <Tabs activeTab={activeTab} onTabChange={onTabChange}>
      <Tabs.List className="flex-wrap">
        {visibleTabs.map((tab) => (
          <Tabs.Tab key={tab.id} id={tab.id}>
            {tab.label}
          </Tabs.Tab>
        ))}
      </Tabs.List>
      <Tabs.Panels>
        {SETTINGS_TABS.map((tab) => {
          const isActiveHighlight =
            !!highlight &&
            tab.id === activeTab &&
            tab.keywords.some((kw) => kw.includes(highlight.toLowerCase()));

          return (
            <Tabs.Panel key={tab.id} id={tab.id}>
              <div className="py-8 text-center text-pf-text-secondary">
                <p className="text-sm">
                  {tab.label} settings will be available here.
                </p>
                <p className="text-xs mt-1 text-pf-text-tertiary">
                  Content migrated in ST-2.
                </p>
                {isActiveHighlight && (
                  <div
                    ref={highlightRef}
                    data-testid="highlight-target"
                    className="mt-4 mx-auto max-w-sm rounded-md border border-amber-300 bg-amber-50 px-4 py-3"
                  >
                    <p className="text-sm font-medium text-amber-800">
                      Highlighted:{' '}
                      <span className="font-semibold capitalize">{highlight}</span>
                    </p>
                  </div>
                )}
              </div>
            </Tabs.Panel>
          );
        })}
      </Tabs.Panels>
    </Tabs>
  );
};
