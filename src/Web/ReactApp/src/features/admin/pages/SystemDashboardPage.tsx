import React from 'react';
import { useSearchParams } from 'react-router';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Tabs } from '@/common/components/ui';
import { SystemLogsContent } from '../components/SystemLogsContent';
import { ObservabilityContent } from '../components/ObservabilityContent';
import { FileHealthContent } from '../components/FileHealthContent';
import { ActivityIcon, HistoryIcon, DatabaseIcon } from '@/common/components/icons/MdiIcons';

type TabId = 'logs' | 'observability' | 'file-health';

const TAB_CONFIG: { id: TabId; label: string; icon: React.ReactNode }[] = [
  { id: 'logs', label: 'System Logs', icon: <HistoryIcon className="w-4 h-4" /> },
  { id: 'observability', label: 'Observability', icon: <ActivityIcon className="w-4 h-4" /> },
  { id: 'file-health', label: 'File Health', icon: <DatabaseIcon className="w-4 h-4" /> },
];

export function SystemDashboardPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const tabParam = searchParams.get('tab');
  const activeTab = TAB_CONFIG.some(t => t.id === tabParam) ? (tabParam as TabId) : 'logs';

  const handleTabChange = (tabId: string) => {
    setSearchParams({ tab: tabId });
  };

  return (
    <PageTemplate
      title="System Dashboard"
      subtitle="Monitor logs, telemetry, and file health in one place"
      icon={ActivityIcon}
    >
      <Tabs activeTab={activeTab} onTabChange={handleTabChange}>
        <Tabs.List>
          {TAB_CONFIG.map(tab => (
            <Tabs.Tab key={tab.id} id={tab.id} icon={tab.icon}>
              {tab.label}
            </Tabs.Tab>
          ))}
        </Tabs.List>

        <Tabs.Panels>
          <Tabs.Panel id="logs">
            <SystemLogsContent />
          </Tabs.Panel>

          <Tabs.Panel id="observability">
            <ObservabilityContent />
          </Tabs.Panel>

          <Tabs.Panel id="file-health">
            <FileHealthContent />
          </Tabs.Panel>
        </Tabs.Panels>
      </Tabs>
    </PageTemplate>
  );
}
