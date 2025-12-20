import React from 'react';
import { useSearchParams } from 'react-router-dom';
import { FileBrowser } from '@/components/files/FileBrowser';
import { PageTemplate } from '@/components/PageTemplate';
import { FileIcon } from '@/components/icons/MdiIcons';

export const FilesPage: React.FC = () => {
  const [searchParams] = useSearchParams();
  const harvestId = searchParams.get('harvest') || undefined;
  const printerId = searchParams.get('printer') || undefined;

  return (
    <PageTemplate
      title="G-code Files"
      subtitle="Browse and manage your harvested G-code files"
      icon={FileIcon}
      maxWidth="max-w-7xl"
    >
      <FileBrowser
        harvestId={harvestId}
        printerId={printerId}
      />
    </PageTemplate>
  );
};