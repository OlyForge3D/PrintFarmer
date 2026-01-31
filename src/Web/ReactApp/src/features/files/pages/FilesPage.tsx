import { useState, useEffect, useCallback, useEffectEvent, useMemo } from 'react';
import { CubeIcon, FileIcon, TrendingUpIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { MasterDetailLayout } from '@/common/components/layout/MasterDetailLayout';
import { ModelsPage } from '@/features/models3d/pages/ModelsPage';
import { GcodeLibraryPage } from '@/features/gcode/pages/GcodeLibraryPage';
import { HarvestPage } from '@/features/gcode/pages/HarvestPage';
import { useLocation } from 'react-router-dom';
import { useSlicer } from '@/hooks/useSlicer';

interface Tab {
  id: 'models' | 'gcode' | 'harvest';
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  description: string;
  requiresSlicer?: boolean;
}

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

export function FilesPage() {
  const location = useLocation();
  const { isSlicerAvailable } = useSlicer();
  
  // Filter tabs based on slicer availability
  const TABS = useMemo(() => {
    return ALL_TABS.filter(tab => !tab.requiresSlicer || isSlicerAvailable);
  }, [isSlicerAvailable]);
  
  // Initialize from localStorage, respecting URL params and slicer availability
  const [activeTab, setActiveTab] = useState<'models' | 'gcode' | 'harvest'>(() => {
    // First check URL params (they take precedence)
    const params = new URLSearchParams(window.location.search);
    const urlTab = params.get('tab') as 'models' | 'gcode' | 'harvest' | null;
    if (urlTab && (urlTab === 'gcode' || urlTab === 'harvest' || urlTab === 'models')) {
      // Will be validated later against available tabs
      return urlTab;
    }
    // Then check localStorage
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
    // Fallback to first available tab (typically 'gcode')
    return availableTabIds[0] ?? 'gcode';
  }, [activeTab, TABS]);
  
  // Keep internal state synced with validated tab
  useEffect(() => {
    if (validActiveTab !== activeTab) {
      // Schedule the state update to avoid synchronous setState in effect
      queueMicrotask(() => {
        setActiveTab(validActiveTab);
        localStorage.setItem('pf.filesPageActiveTab', validActiveTab);
      });
    }
  }, [validActiveTab, activeTab]);

  // Persist tab change to localStorage
  const handleTabChange = useCallback((tab: 'models' | 'gcode' | 'harvest') => {
    setActiveTab(tab);
    localStorage.setItem('pf.filesPageActiveTab', tab);
  }, []);

  // Sync active tab with URL params when location changes
  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const tab = params.get('tab') as 'models' | 'gcode' | 'harvest' | null;
    if (tab && TABS.some(t => t.id === tab)) {
      // Schedule the state update to avoid synchronous setState in effect
      queueMicrotask(() => {
        setActiveTab(tab);
        localStorage.setItem('pf.filesPageActiveTab', tab);
      });
    }
  }, [location.search, TABS]);

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
  }, [handleKeyDown]);

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
