import React from 'react';
import { useSearchParams } from 'react-router-dom';
import { GcodeFileBrowser } from '@/features/gcode/components/GcodeFileBrowser';
import { PageTemplate } from '@/common/components/PageTemplate';
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { FileIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { FloatingActionButton } from '@/common/components/FloatingActionButton';
import { Breadcrumbs } from '@/common/components/Breadcrumbs';

export const GcodeLibraryPage: React.FC = () => {
  const [searchParams] = useSearchParams();
  const { viewMode, setViewMode } = useViewModePreference('printfarmer-gcode-viewmode');
  const harvestId = searchParams.get('harvest') || undefined;
  const printerId = searchParams.get('printer') || undefined;

  // Keyboard shortcuts for G-code library actions
  useKeyboardShortcuts([
    {
      key: 'u',
      handler: () => {
        // Upload is handled by GcodeFileBrowser
        const uploadButton = document.querySelector('[title="Upload files"]') as HTMLButtonElement;
        uploadButton?.click();
      },
      description: 'Upload new G-code file'
    },
    {
      key: 'v',
      handler: () => {
        const viewModes: Array<'grid' | 'explorer'> = ['grid', 'explorer'];
        const currentIndex = viewModes.indexOf(viewMode as 'grid' | 'explorer');
        const nextIndex = (currentIndex + 1) % viewModes.length;
        setViewMode(viewModes[nextIndex]);
      },
      description: 'Cycle view mode (Grid → Explorer)'
    }
  ]);

  return (
    <PageTemplate
      title="G-code Library"
      subtitle="Browse and manage your G-code files"
      icon={FileIcon}
    >
      <div className="space-y-4 flex flex-col h-full">
        {/* Breadcrumbs */}
        <Breadcrumbs
          items={[
            { label: 'Dashboard', href: '/' },
            { label: 'Files', href: '/files' },
            { label: 'G-Code', current: true }
          ]}
        />

        {/* Content */}
        <div className="flex-1 min-h-0">
          <GcodeFileBrowser
            harvestId={harvestId}
            printerId={printerId}
            viewMode={viewMode as 'grid' | 'explorer'}
            onViewModeChange={setViewMode}
          />
        </div>
      </div>

      {/* Floating Action Button for Upload */}
      <FloatingActionButton
        icon={PlusIcon}
        onClick={() => {
          const uploadButton = document.querySelector('[title="Upload files"]') as HTMLButtonElement;
          uploadButton?.click();
        }}
        label="Upload G-Code"
        position="bottom-right"
        variant="primary"
      />
    </PageTemplate>
  );
};