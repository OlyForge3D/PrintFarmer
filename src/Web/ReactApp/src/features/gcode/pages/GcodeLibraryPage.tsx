import React, { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { FileBrowser } from '@/features/gcode/components/FileBrowser';
import { PageTemplate } from '@/common/components/PageTemplate';
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { FileIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { FloatingActionButton } from '@/common/components/FloatingActionButton';
import { Breadcrumbs } from '@/common/components/Breadcrumbs';
import { GcodeUploadModal } from '@/common/components/modals/GcodeUploadModal';

export const GcodeLibraryPage: React.FC = () => {
  const [searchParams] = useSearchParams();
  const [showUploadModal, setShowUploadModal] = useState(false);
  const { viewMode, setViewMode } = useViewModePreference('printfarmer-gcode-viewmode');
  const harvestId = searchParams.get('harvest') || undefined;
  const printerId = searchParams.get('printer') || undefined;

  // Keyboard shortcuts for G-code library actions
  useKeyboardShortcuts([
    {
      key: 'u',
      handler: () => setShowUploadModal(true),
      description: 'Upload new G-code file'
    },
    {
      key: 'v',
      handler: () => {
        const viewModes: Array<'grid' | 'list' | 'explorer'> = ['grid', 'list', 'explorer'];
        const currentIndex = viewModes.indexOf(viewMode);
        const nextIndex = (currentIndex + 1) % viewModes.length;
        setViewMode(viewModes[nextIndex]);
      },
      description: 'Cycle view mode (Grid → List → Explorer)'
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
          <FileBrowser
            harvestId={harvestId}
            printerId={printerId}
            viewMode={viewMode}
            onViewModeChange={setViewMode}
          />
        </div>
      </div>

      {/* Floating Action Button for Upload */}
      <FloatingActionButton
        icon={PlusIcon}
        onClick={() => setShowUploadModal(true)}
        label="Upload G-Code"
        position="bottom-right"
        variant="primary"
      />

      {/* G-Code Upload Modal */}
      <GcodeUploadModal
        isOpen={showUploadModal}
        onClose={() => setShowUploadModal(false)}
        onFilesSelected={() => {
          setShowUploadModal(false);
        }}
        harvestId={harvestId}
        printerId={printerId}
      />
    </PageTemplate>
  );
};