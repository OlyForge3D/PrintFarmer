import { useState, useEffect, useCallback } from 'react';
import { useSearchParams } from 'react-router';
import { PackageIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { PageTemplate } from '@/common/components/PageTemplate';
import { FilamentsTab } from '@/features/catalog/components/FilamentsTab';
import { SpoolsTab } from '@/features/catalog/components/SpoolsTab';

type TabId = 'filaments' | 'spools';

interface Tab {
  id: TabId;
  label: string;
  description: string;
}

const TABS: Tab[] = [
  {
    id: 'filaments',
    label: 'Filaments',
    description: 'Filament product definitions from Spoolman'
  },
  {
    id: 'spools',
    label: 'Spools',
    description: 'Physical spool inventory with remaining weight and usage'
  }
];

/**
 * SpoolsPage — Container page with tabbed navigation for Filaments and Spools.
 * Defaults to the Filaments tab. Active tab is persisted via URL search params.
 */
export function SpoolsPage() {
  const [searchParams, setSearchParams] = useSearchParams();

  const [activeTab, setActiveTab] = useState<TabId>(() => {
    const urlTab = searchParams.get('tab');
    if (urlTab === 'filaments' || urlTab === 'spools') return urlTab;
    const saved = localStorage.getItem('pf.spoolsPageActiveTab');
    if (saved === 'filaments' || saved === 'spools') return saved as TabId;
    return 'filaments';
  });

  // Sync URL → state when search params change externally
  useEffect(() => {
    const urlTab = searchParams.get('tab');
    if (urlTab === 'filaments' || urlTab === 'spools') {
      if (urlTab !== activeTab) {
        queueMicrotask(() => {
          setActiveTab(urlTab);
          localStorage.setItem('pf.spoolsPageActiveTab', urlTab);
        });
      }
    }
  }, [searchParams, activeTab]);

  const handleTabChange = useCallback((tab: TabId) => {
    setActiveTab(tab);
    localStorage.setItem('pf.spoolsPageActiveTab', tab);
    setSearchParams({ tab }, { replace: true });
  }, [setSearchParams]);

  const currentTab = TABS.find(t => t.id === activeTab) ?? TABS[0];

  return (
    <PageTemplate
      title="Filament Inventory"
      subtitle={currentTab.description}
      icon={PackageIcon}
    >
      {/* Tab bar */}
      <div className="flex gap-0 border-b border-pf-border mb-4" role="tablist" aria-label="Filament inventory tabs">
        {TABS.map(tab => {
          const isActive = activeTab === tab.id;
          return (
            <Button
              key={tab.id}
              onClick={() => handleTabChange(tab.id)}
              variant="tab"
              className={`
                ${isActive
                  ? 'border-b-2 border-b-pf-accent text-pf-accent'
                  : 'text-pf-text-secondary hover:text-pf-text-primary'
                }
              `}
              aria-selected={isActive}
              role="tab"
              aria-controls={`tabpanel-${tab.id}`}
              id={`tab-${tab.id}`}
            >
              {tab.label}
            </Button>
          );
        })}
      </div>

      {/* Tab content */}
      <div
        role="tabpanel"
        id={`tabpanel-${activeTab}`}
        aria-labelledby={`tab-${activeTab}`}
      >
        {activeTab === 'filaments' ? <FilamentsTab /> : <SpoolsTab />}
      </div>
    </PageTemplate>
  );
}
