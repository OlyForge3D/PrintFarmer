import React from 'react';
import { Download } from 'lucide-react';
import { Button } from '@/components/ui';
import { useImportFilamentTypesFromSpoolman } from '@/hooks/useApi';
import { toast } from 'sonner';

interface SpoolmanFilamentImportButtonProps {
  className?: string;
  onImportSuccess?: () => void;
}

export function SpoolmanFilamentImportButton({ 
  className = '', 
  onImportSuccess 
}: SpoolmanFilamentImportButtonProps) {
  const importMutation = useImportFilamentTypesFromSpoolman();

  const handleImport = async () => {
    try {
      const result = await importMutation.mutateAsync();
      
      if (result.importedCount > 0) {
        toast.success(
          `Imported ${result.importedCount} new filament types from Spoolman`,
          {
            description: `Materials: ${result.importedNames.join(', ')}. Skipped ${result.skippedCount} existing types.`
          }
        );
      } else {
        toast.info(
          `No new filament types to import`,
          {
            description: `Found ${result.totalSpoolmanMaterials} materials in Spoolman, but all ${result.skippedCount} already exist in PrintFarmer.`
          }
        );
      }
      
      onImportSuccess?.();
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to import filament types';
      toast.error('Import failed', { description: message });
    }
  };

  return (
    <Button
      type="button"
      variant="primary"
      onClick={handleImport}
      disabled={importMutation.status === 'pending'}
      className={className}
      title="Import unique filament types from Spoolman to maintain parity between applications"
    >
      {importMutation.status === 'pending' ? (
        <>
          <div className="w-4 h-4 mr-2 border-2 border-white border-t-transparent rounded-full animate-spin" />
          Importing...
        </>
      ) : (
        <>
          <Download className="w-4 h-4 mr-2" />
          Import from Spoolman
        </>
      )}
    </Button>
  );
}