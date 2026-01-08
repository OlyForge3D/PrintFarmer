import React from 'react';
import { useSearchParams } from 'react-router-dom';
import { FileBrowser } from '@/features/gcode/components/FileBrowser';
import { PageTemplate } from '@/common/components/PageTemplate';
import { FileIcon } from '@/common/components/icons/MdiIcons';

export const GcodeLibraryPage: React.FC = () => {
  const [searchParams] = useSearchParams();
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
    </PageTemplate>
  );
};