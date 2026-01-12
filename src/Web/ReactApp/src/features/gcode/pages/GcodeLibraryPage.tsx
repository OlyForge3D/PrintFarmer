import React, { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { FileBrowser } from '@/features/gcode/components/FileBrowser';
import { PageTemplate } from '@/common/components/PageTemplate';
import { FileIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { FloatingActionButton } from '@/common/components/FloatingActionButton';
import { GcodeUploadModal } from '@/common/components/modals/GcodeUploadModal';

export const GcodeLibraryPage: React.FC = () => {
  const [searchParams] = useSearchParams();
  const [showUploadModal, setShowUploadModal] = useState(false);
  const harvestId = searchParams.get('harvest') || undefined;
  const printerId = searchParams.get('printer') || undefined;

  return (
    <PageTemplate
      title="G-code Library"
      subtitle="Browse and manage your G-code files"
      icon={FileIcon}
    >
      <FileBrowser
        harvestId={harvestId}
        printerId={printerId}
      />

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
      />
    </PageTemplate>
  );
};