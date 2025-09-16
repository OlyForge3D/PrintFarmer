import React from 'react';
import { useSearchParams } from 'react-router-dom';
import { FileBrowser } from '@/components/files/FileBrowser';

export const FilesPage: React.FC = () => {
  const [searchParams] = useSearchParams();
  const harvestId = searchParams.get('harvest') || undefined;
  const printerId = searchParams.get('printer') || undefined;

  return (
    <div className="p-6">
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-pf-text-primary">G-code Files</h1>
        <p className="mt-1 text-sm text-pf-text-secondary">
          Browse and manage your harvested G-code files
        </p>
      </div>

      <FileBrowser
        harvestId={harvestId}
        printerId={printerId}
      />
    </div>
  );
};