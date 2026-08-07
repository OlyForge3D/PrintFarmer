import React, { Suspense, useState, useEffect, useCallback } from 'react';
import { useSearchParams, useParams, useNavigate } from 'react-router';
import { PackageIcon, BarcodeScanIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { PageTemplate } from '@/common/components/PageTemplate';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';
import type { SpoolmanSpool } from '@/types/api';

type TabId = 'filaments' | 'spools' | 'clusters';
type FilamentsTabComponent = typeof import('@/features/filamentManagement/components/FilamentsTab')['FilamentsTab'];
type SpoolsTabComponent = typeof import('@/features/filamentManagement/components/SpoolsTab')['SpoolsTab'];
type MaterialClustersTabComponent = typeof import('@/features/filamentManagement/components/MaterialClustersTab')['MaterialClustersTab'];
type ScanSpoolModalComponent = typeof import('@/features/filamentManagement/components/ScanSpoolModal')['ScanSpoolModal'];

const FilamentsTab = lazyWithPreload<React.ComponentProps<FilamentsTabComponent>, FilamentsTabComponent>(
  () => import('@/features/filamentManagement/components/FilamentsTab').then((module) => ({ default: module.FilamentsTab })),
);
const SpoolsTab = lazyWithPreload<React.ComponentProps<SpoolsTabComponent>, SpoolsTabComponent>(
  () => import('@/features/filamentManagement/components/SpoolsTab').then((module) => ({ default: module.SpoolsTab })),
);
const MaterialClustersTab = lazyWithPreload<React.ComponentProps<MaterialClustersTabComponent>, MaterialClustersTabComponent>(
  () => import('@/features/filamentManagement/components/MaterialClustersTab').then((module) => ({ default: module.MaterialClustersTab })),
);
const ScanSpoolModal = lazyWithPreload<React.ComponentProps<ScanSpoolModalComponent>, ScanSpoolModalComponent>(
  () => import('@/features/filamentManagement/components/ScanSpoolModal').then((module) => ({ default: module.ScanSpoolModal })),
);

const TAB_COMPONENTS = {
  filaments: FilamentsTab,
  spools: SpoolsTab,
  clusters: MaterialClustersTab,
} satisfies Record<TabId, typeof FilamentsTab | typeof SpoolsTab | typeof MaterialClustersTab>;

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
  },
  {
    id: 'clusters',
    label: 'Material Clusters',
    description: 'Group equivalent materials for smarter auto-dispatch matching'
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
    if (tabId === 'filaments' || tabId === 'spools' || tabId === 'clusters') return tabId;
    const urlTab = searchParams.get('tab');
    if (urlTab === 'filaments' || urlTab === 'spools' || urlTab === 'clusters') return urlTab;
    const saved = localStorage.getItem('pf.spoolsPageActiveTab');
    if (saved === 'filaments' || saved === 'spools' || saved === 'clusters') return saved as TabId;
    return 'filaments';
  });

  // Sync URL → state when path param changes externally
  useEffect(() => {
    if (tabId === 'filaments' || tabId === 'spools' || tabId === 'clusters') {
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
  const ActiveTab = TAB_COMPONENTS[activeTab];

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
          onFocus={() => void ScanSpoolModal.preload()}
          onPointerEnter={() => void ScanSpoolModal.preload()}
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
              onFocus={() => void TAB_COMPONENTS[tab.id].preload()}
              onPointerEnter={() => void TAB_COMPONENTS[tab.id].preload()}
              variant="tab"
              className={`transition-all duration-200 motion-reduce:transition-none
                ${isActive
                  ? 'border-b-2 border-b-pf-accent text-pf-accent enabled:hover:bg-pf-bg-1'
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
        className="transition-opacity duration-200 motion-reduce:transition-none"
      >
        <Suspense
          fallback={(
            <div className="flex min-h-48 items-center justify-center" role="status" aria-label={`Loading ${currentTab.label}`}>
              <div className="pf-animate-spin h-8 w-8 rounded-full border-b-2 border-pf-accent" />
            </div>
          )}
        >
          <ActiveTab />
        </Suspense>
      </div>

      {scanModalOpen && (
        <Suspense fallback={null}>
          <ScanSpoolModal
            isOpen={scanModalOpen}
            onClose={() => setScanModalOpen(false)}
            onSpoolFound={handleSpoolFound}
          />
        </Suspense>
      )}
    </PageTemplate>
  );
}
