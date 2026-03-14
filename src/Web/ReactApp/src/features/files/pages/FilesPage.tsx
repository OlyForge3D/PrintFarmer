import { useState, useEffect, useCallback, useEffectEvent, useMemo } from 'react';
import { CubeIcon, FileIcon, TrendingUpIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { MasterDetailLayout } from '@/common/components/layout/MasterDetailLayout';
import { ModelsPage } from '@/features/models3d/pages/ModelsPage';
import { GcodeLibraryPage } from '@/features/gcode/pages/GcodeLibraryPage';
import { HarvestPage } from '@/features/gcode/pages/HarvestPage';
import { useLocation, useNavigate } from 'react-router';
import { useSlicer } from '@/hooks/useSlicer';
import { useSystemCapabilities } from '@/common/hooks/useSystemCapabilities';

type TabId = 'models' | 'gcode' | 'harvest';

interface Tab {
  id: TabId;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  description: string;
  requiresSlicer?: boolean;
}

// Map URL path segments to tab IDs
const PATH_TO_TAB: Record<string, TabId> = {
  'models': 'models',
  '3d-models': 'models',
  'gcode': 'gcode',
  'harvest': 'harvest',
};

const ALL_TABS: Tab[] = [
  {
    id: 'models',
    label: '3D Models',
    icon: CubeIcon,
    description: 'Manage your 3D model files',
    requiresSlicer: true
  },
  {
    id: 'gcode',
    label: 'G-Code',
    icon: FileIcon,
    description: 'View and manage sliced G-code files'
  },
  {
    id: 'harvest',
    label: 'Print Harvest',
    icon: TrendingUpIcon,
    description: 'Track print results and harvest data'
  }
];

/** Extract tab ID from the current URL path or query params */
function resolveTabFromLocation(pathname: string, search: string): TabId | null {
  // 1. Check path: /files/projects → "projects"
  const segment = pathname.replace(/^\/files\/?/, '').split('/')[0];
  if (segment && PATH_TO_TAB[segment]) {
    return PATH_TO_TAB[segment];
  }
  // 2. Fallback: ?tab=projects
  const params = new URLSearchParams(search);
  const urlTab = params.get('tab');
  if (urlTab && PATH_TO_TAB[urlTab]) {
    return PATH_TO_TAB[urlTab];
  }
  return null;
}

export function FilesPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const { isSlicerAvailable } = useSlicer();
  const { data: capabilities } = useSystemCapabilities();
  
  // Filter tabs based on slicer availability AND platform capabilities
  // Use `!== false` so tabs are visible before the capabilities query resolves
  const TABS = useMemo(() => {
    return ALL_TABS.filter(tab => {
      if (tab.requiresSlicer && !isSlicerAvailable) return false;
      if (tab.requiresSlicer && capabilities?.slicingEnabled === false) return false;
      if (tab.id === 'models' && capabilities?.modelFilesEnabled === false) return false;
      return true;
    });
  }, [isSlicerAvailable, capabilities]);
  
  // Initialize from URL path, then query param, then localStorage
  const [activeTab, setActiveTab] = useState<TabId>(() => {
    const fromUrl = resolveTabFromLocation(window.location.pathname, window.location.search);
    if (fromUrl) return fromUrl;
    const saved = localStorage.getItem('pf.filesPageActiveTab');
    if (saved === 'models' || saved === 'gcode' || saved === 'harvest') {
      return saved;
    }
    return 'gcode';
  });
  
  // Compute valid active tab - if current tab is unavailable, use first available
  const validActiveTab = useMemo(() => {
    const availableTabIds = TABS.map(t => t.id);
    if (availableTabIds.includes(activeTab)) {
      return activeTab;
    }
    return availableTabIds[0] ?? 'gcode';
  }, [activeTab, TABS]);
  
  // Keep internal state synced with validated tab
  useEffect(() => {
    if (validActiveTab !== activeTab) {
      queueMicrotask(() => {
        setActiveTab(validActiveTab);
        localStorage.setItem('pf.filesPageActiveTab', validActiveTab);
      });
    }
  }, [validActiveTab, activeTab]);

  // Navigate to the tab's URL path
  const handleTabChange = useCallback((tab: TabId) => {
    setActiveTab(tab);
    localStorage.setItem('pf.filesPageActiveTab', tab);
    navigate(`/files/${tab}`, { replace: true });
  }, [navigate]);

  // Sync active tab when URL changes (e.g., browser back/forward)
  useEffect(() => {
    const fromUrl = resolveTabFromLocation(location.pathname, location.search);
    if (fromUrl && TABS.some(t => t.id === fromUrl)) {
      queueMicrotask(() => {
        setActiveTab(fromUrl);
        localStorage.setItem('pf.filesPageActiveTab', fromUrl);
      });
    }
  }, [location.pathname, location.search, TABS]);

  // React 19: useEffectEvent to handle keyboard input without dependency issues
  const handleKeyDown = useEffectEvent((e: KeyboardEvent) => {
    if (e.key === 't' && !['input', 'textarea'].includes((e.target as HTMLElement).tagName.toLowerCase())) {
      e.preventDefault();
      // Only cycle through available tabs
      const tabIds = TABS.map(t => t.id);
      const currentIndex = tabIds.indexOf(validActiveTab);
      const nextIndex = (currentIndex + 1) % tabIds.length;
      handleTabChange(tabIds[nextIndex]);
    }
  });

  // Keyboard navigation: 't' to cycle tabs - React 19: Simplified with useEffectEvent
  useEffect(() => {
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
   
  }, []);

  const currentTab = TABS.find(t => t.id === validActiveTab)!;

  const renderContent = () => {
    switch (validActiveTab) {
      case 'gcode':
        return <GcodeLibraryPage />;
      case 'harvest':
        return <HarvestPage />;
      case 'models':
      default:
        return <ModelsPage />;
    }
  };

  // Master panel: Tab navigation (rendered in horizontal layout)
  const masterContent = (
    <div className="px-6 pt-1 flex gap-0">
      {TABS.map((tab) => {
        const TabIcon = tab.icon;
        const isActive = validActiveTab === tab.id;
        return (
          <Button
            key={tab.id}
            onClick={() => handleTabChange(tab.id)}
            variant="tab"
            iconLeft={<TabIcon className="w-4 h-4" />}
            className={`
              ${isActive
                ? 'border-b-2 border-b-pf-accent text-pf-accent'
                : 'text-pf-text-secondary hover:text-pf-text-primary'
              }
            `}
            aria-selected={isActive}
            role="tab"
            title="Press 't' to cycle tabs"
          >
            {tab.label}
          </Button>
        );
      })}
    </div>
  );

  // Detail panel: Content area
  const detailContent = (
    <main className="flex-1 overflow-hidden">
      <div className="h-full overflow-y-auto">
        {renderContent()}
      </div>
    </main>
  );

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Page Header - Dynamic based on active tab */}
      <div className="border-b border-pf-border bg-pf-bg-1 px-6 py-4">
        <h1 className="text-2xl font-bold text-pf-text-primary">{currentTab.label}</h1>
        <p className="text-sm text-pf-text-secondary mt-1">{currentTab.description}</p>
      </div>

      {/* Horizontal Master-Detail Layout: Tabs on top, content below */}
      <MasterDetailLayout
        orientation="horizontal"
        masterHeight="h-auto"
        master={masterContent}
        detail={detailContent}
        hasDetail={true}
        masterClassName="bg-pf-bg-1"
      />
    </div>
  );
}
