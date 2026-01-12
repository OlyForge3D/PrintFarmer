import { useState, useEffect } from 'react';
import { CubeIcon, FileIcon, TrendingUpIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
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
  const [activeTab, setActiveTab] = useState<'models' | 'gcode' | 'harvest'>('models');

  // Sync active tab with URL params if present
  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const tab = params.get('tab') as 'models' | 'gcode' | 'harvest' | null;
    if (tab && TABS.some(t => t.id === tab)) {
      setActiveTab(tab);
    }
  }, [location.search]);

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

  const currentTab = TABS.find(t => t.id === activeTab)!;

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Page Header */}
      <div className="border-b border-pf-border bg-pf-bg-1 px-6 py-4">
        <h1 className="text-2xl font-bold text-pf-text-primary">Files</h1>
        <p className="text-sm text-pf-text-secondary mt-1">{currentTab.description}</p>
      </div>

      {/* Tabs Navigation */}
      <div className="border-b border-pf-border bg-pf-bg-1 px-6">
        <div className="flex gap-0">
          {TABS.map((tab) => {
            const TabIcon = tab.icon;
            const isActive = activeTab === tab.id;
            return (
              <Button
                key={tab.id}
                onClick={() => setActiveTab(tab.id)}
                variant="tab"
                className={`
                  flex items-center gap-2
                  ${isActive
                    ? 'border-b-2 border-pf-accent text-pf-accent'
                    : 'text-pf-text-secondary hover:text-pf-text-primary'
                  }
                `}
                aria-selected={isActive}
                role="tab"
              >
                <TabIcon className="w-4 h-4" />
                <span>{tab.label}</span>
              </Button>
            );
          })}
        </div>
      </div>

      {/* Content Area */}
      <main className="flex-1 overflow-hidden">
        <div className="h-full overflow-y-auto">
          {renderContent()}
        </div>
      </main>
    </div>
  );
}
