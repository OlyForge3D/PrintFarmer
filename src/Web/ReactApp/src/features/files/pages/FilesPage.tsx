import { useState, useEffect, useCallback, useEffectEvent } from 'react';
import { CubeIcon, FileIcon, TrendingUpIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { Breadcrumbs } from '@/common/components/Breadcrumbs';
import { MasterDetailLayout } from '@/common/components/layout/MasterDetailLayout';
import { ModelsPage } from '@/features/models3d/pages/ModelsPage';
import { GcodeLibraryPage } from '@/features/gcode/pages/GcodeLibraryPage';
import { HarvestPage } from '@/features/gcode/pages/HarvestPage';
import { useLocation } from 'react-router-dom';

interface Tab {
  id: 'models' | 'gcode' | 'harvest';
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  description: string;
}

const TABS: Tab[] = [
  {
    id: 'models',
    label: '3D Models',
    icon: CubeIcon,
    description: 'Manage your 3D model files'
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
  
  // Initialize from localStorage, fallback to 'models'
  const [activeTab, setActiveTab] = useState<'models' | 'gcode' | 'harvest'>(() => {
    const saved = localStorage.getItem('pf.filesPageActiveTab');
    if (saved === 'models' || saved === 'gcode' || saved === 'harvest') {
      return saved;
    }
    return 'models';
  });

  // Persist tab change to localStorage
  const handleTabChange = useCallback((tab: 'models' | 'gcode' | 'harvest') => {
    setActiveTab(tab);
    localStorage.setItem('pf.filesPageActiveTab', tab);
  }, []);

  // Sync active tab with URL params if present (URL takes precedence)
  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const tab = params.get('tab') as 'models' | 'gcode' | 'harvest' | null;
    if (tab && TABS.some(t => t.id === tab)) {
      setActiveTab(tab);
      localStorage.setItem('pf.filesPageActiveTab', tab);
    }
  }, [location.search]);

  // React 19: useEffectEvent to handle keyboard input without dependency issues
  const handleKeyDown = useEffectEvent((e: KeyboardEvent) => {
    if (e.key === 't' && !['input', 'textarea'].includes((e.target as HTMLElement).tagName.toLowerCase())) {
      e.preventDefault();
      const tabIds: Array<'models' | 'gcode' | 'harvest'> = ['models', 'gcode', 'harvest'];
      const currentIndex = tabIds.indexOf(activeTab);
      const nextIndex = (currentIndex + 1) % tabIds.length;
      handleTabChange(tabIds[nextIndex]);
    }
  });

  // Keyboard navigation: 't' to cycle tabs - React 19: Simplified with useEffectEvent
  useEffect(() => {
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [handleKeyDown]);

  const currentTab = TABS.find(t => t.id === activeTab)!;

  const renderContent = () => {
    switch (activeTab) {
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
        const isActive = activeTab === tab.id;
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
      {/* Breadcrumb Navigation */}
      <div className="px-6 py-4 border-b border-pf-border bg-pf-bg-1">
        <Breadcrumbs
          items={[
            { label: 'Dashboard', href: '/' },
            { label: 'Files', current: true }
          ]}
        />
      </div>

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
