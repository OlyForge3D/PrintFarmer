import { useState, useEffect, useCallback } from 'react';
import { useSearchParams, useParams, useNavigate } from 'react-router';
import { PackageIcon, BarcodeScanIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { PageTemplate } from '@/common/components/PageTemplate';
import { FilamentsTab } from '@/features/filamentManagement/components/FilamentsTab';
import { SpoolsTab } from '@/features/filamentManagement/components/SpoolsTab';
import { ScanSpoolModal } from '@/features/filamentManagement/components/ScanSpoolModal';
import type { SpoolmanSpool } from '@/types/api';

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
 * FilamentManagementPage — Container page with tabbed navigation for Filaments and Spools.
 * Defaults to the Filaments tab. Active tab is persisted via URL search params.
 */
export function FilamentManagementPage() {
  const [searchParams] = useSearchParams();
  const { tabId } = useParams<{ tabId?: string }>();
  const navigate = useNavigate();
  const [scanModalOpen, setScanModalOpen] = useState(false);

  const [activeTab, setActiveTab] = useState<TabId>(() => {
    if (tabId === 'filaments' || tabId === 'spools') return tabId;
    const urlTab = searchParams.get('tab');
    if (urlTab === 'filaments' || urlTab === 'spools') return urlTab;
    const saved = localStorage.getItem('pf.spoolsPageActiveTab');
    if (saved === 'filaments' || saved === 'spools') return saved as TabId;
    return 'filaments';
  });

  // Sync URL → state when path param changes externally
  useEffect(() => {
    if (tabId === 'filaments' || tabId === 'spools') {
      if (tabId !== activeTab) {
        queueMicrotask(() => {
          setActiveTab(tabId);
          localStorage.setItem('pf.spoolsPageActiveTab', tabId);
        });
      }
    }
  }, [tabId, activeTab]);

  const handleTabChange = useCallback((tab: TabId) => {
    setActiveTab(tab);
    localStorage.setItem('pf.spoolsPageActiveTab', tab);
    navigate(`/spools/${tab}`, { replace: true });
  }, [navigate]);

  const currentTab = TABS.find(t => t.id === activeTab) ?? TABS[0];

  const handleSpoolFound = useCallback((spool: SpoolmanSpool) => {
    // Switch to Spools tab so the user can find the matched spool
    handleTabChange('spools');
    // The SpoolsTab will show the full inventory — the toast already confirms the find
    void spool;
  }, [handleTabChange]);

  return (
    <PageTemplate
      title="Filament Inventory"
      subtitle={currentTab.description}
      icon={PackageIcon}
      actions={
        <Button
          variant="secondary"
          size="sm"
          onClick={() => setScanModalOpen(true)}
          iconLeft={<BarcodeScanIcon className="w-4 h-4" />}
        >
          Scan
        </Button>
      }
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

      <ScanSpoolModal
        isOpen={scanModalOpen}
        onClose={() => setScanModalOpen(false)}
        onSpoolFound={handleSpoolFound}
      />
    </PageTemplate>
  );
}
