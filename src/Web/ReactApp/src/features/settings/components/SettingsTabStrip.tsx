import { Tabs } from '@/common/components/ui';
import { SETTINGS_TABS } from '@/features/settings/types';

interface SettingsTabStripProps {
  activeTab: string;
  onTabChange: (tabId: string) => void;
  filteredTabIds?: string[];
}

export const SettingsTabStrip: React.FC<SettingsTabStripProps> = ({
  activeTab,
  onTabChange,
  filteredTabIds,
}) => {
  const visibleTabs = filteredTabIds
    ? SETTINGS_TABS.filter((t) => filteredTabIds.includes(t.id))
    : SETTINGS_TABS;

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
        {SETTINGS_TABS.map((tab) => (
          <Tabs.Panel key={tab.id} id={tab.id}>
            <div className="py-8 text-center text-pf-text-secondary">
              <p className="text-sm">
                {tab.label} settings will be available here.
              </p>
              <p className="text-xs mt-1 text-pf-text-tertiary">
                Content migrated in ST-2.
              </p>
            </div>
          </Tabs.Panel>
        ))}
      </Tabs.Panels>
    </Tabs>
  );
};
