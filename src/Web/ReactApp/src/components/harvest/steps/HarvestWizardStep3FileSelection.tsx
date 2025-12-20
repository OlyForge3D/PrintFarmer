import React, { useState } from 'react';
import { CheckCircleIcon, AlertCircleIcon, LoaderIcon } from '@/components/icons/MdiIcons';
import type { HarvestOptions, HarvestDiscoveredFile } from '../HarvestWizard';
import { Button } from '@/components/ui/Button';
import { Alert } from '@/components/ui/Alert';

interface HarvestWizardStep3FileSelectionProps {
  printerId?: string;
  options?: HarvestOptions;
  files: HarvestDiscoveredFile[];
  isDiscovering: boolean;
  onComplete: (selectedFileIds: string[]) => void;
}

export function HarvestWizardStep3FileSelection({
  files,
  isDiscovering,
  onComplete,
}: HarvestWizardStep3FileSelectionProps) {
  const [selectedFileIds, setSelectedFileIds] = useState<Set<string>>(new Set());
  const [allSelected, setAllSelected] = useState(false);

  // Don't auto-select files - let users manually choose what to import
  // Previously this was auto-selecting all files, which could explain why
  // all files were being imported even when user deselected most of them

  const handleToggleFile = (fileId: string) => {
    const newSelected = new Set(selectedFileIds);
    if (newSelected.has(fileId)) {
      newSelected.delete(fileId);
    } else {
      newSelected.add(fileId);
    }
    setSelectedFileIds(newSelected);
    setAllSelected(newSelected.size === files.length && files.length > 0);
  };

  const handleToggleAll = () => {
    if (allSelected) {
      setSelectedFileIds(new Set());
      setAllSelected(false);
    } else {
      const ids = new Set(files.map(f => f.id));
      setSelectedFileIds(ids);
      setAllSelected(true);
    }
  };

  const handleContinue = () => {
    if (selectedFileIds.size > 0) {
      const selectedArray = Array.from(selectedFileIds);
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
        console.info(`[Step3] User selected ${selectedArray.length} files:`, selectedArray);
      }
      // Complete step 3 and pass selected file IDs
      onComplete(selectedArray);
    }
  };

  const formatFileSize = (bytes: number) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return Math.round((bytes / Math.pow(k, i)) * 100) / 100 + ' ' + sizes[i];
  };

  return (
    <div className="space-y-4">
      <div>
        <p className="text-sm text-pf-text-secondary mb-4">
          Discovering G-code files on the printer. Select the files you want to import.
        </p>

        {isDiscovering && (
          <Alert type="info">
            <div className="flex items-center gap-3">
              <LoaderIcon className="w-5 h-5 animate-spin" />
              <span>Scanning printer for G-code files...</span>
            </div>
          </Alert>
        )}

        {!isDiscovering && files.length > 0 && (
          <Alert type="success">
            <div className="flex items-center gap-3">
              <CheckCircleIcon className="w-5 h-5" />
              <span className="font-medium">Scan complete. {files.length} file{files.length !== 1 ? 's' : ''} discovered.</span>
            </div>
          </Alert>
        )}

        {!isDiscovering && files.length === 0 && (
          <Alert type="warning">
            <div className="flex items-center gap-3">
              <AlertCircleIcon className="w-5 h-5" />
              <span>No G-code files found on the printer.</span>
            </div>
          </Alert>
        )}
      </div>

      {files.length > 0 && (
        <div className="space-y-3">
          <div className="flex items-center justify-between p-3 bg-pf-surface border border-pf-border rounded-lg">
            <label htmlFor="selectAll" className="flex items-center gap-3 cursor-pointer">
              {/* eslint-disable-next-line local/pf-no-raw-html-controls */}
              <input
                id="selectAll"
                type="checkbox"
                checked={allSelected}
                onChange={handleToggleAll}
                title="Select all files"
                className="rounded border-pf-border"
              />
              <span className="text-sm font-medium text-pf-text-primary">
                Select All ({selectedFileIds.size}/{files.length})
              </span>
            </label>
          </div>

          <div className="max-h-96 overflow-y-auto space-y-2 border border-pf-border rounded-lg p-3 bg-pf-bg">
            {files.map(file => (
              <label
                key={file.id}
                className="flex items-start gap-3 p-3 hover:bg-pf-hover rounded cursor-pointer transition-colors"
              >
                {/* eslint-disable-next-line local/pf-no-raw-html-controls */}
                <input
                  type="checkbox"
                  checked={selectedFileIds.has(file.id)}
                  onChange={() => handleToggleFile(file.id)}
                  title={`Select ${file.name}`}
                  className="rounded border-pf-border mt-1 flex-shrink-0"
                />
                <div className="flex-1 min-w-0">
                  <div className="font-medium text-pf-text-primary truncate">
                    {file.name}
                  </div>
                  <div className="text-xs text-pf-text-secondary mt-1 space-y-1">
                    <div>Size: {formatFileSize(file.size)}</div>
                    {file.slicerName && <div>Slicer: {file.slicerName}</div>}
                    {file.material && <div>Material: {file.material}</div>}
                  </div>
                </div>
              </label>
            ))}
          </div>

          {selectedFileIds.size > 0 && (
            <Alert type="success">
              <div className="flex items-center gap-2">
                <CheckCircleIcon className="w-5 h-5 flex-shrink-0" />
                <span>
                  {selectedFileIds.size} file{selectedFileIds.size !== 1 ? 's' : ''} selected for import
                </span>
              </div>
            </Alert>
          )}

          {selectedFileIds.size === 0 && files.length > 0 && (
            <Alert type="warning">
              <div className="flex items-center gap-2">
                <AlertCircleIcon className="w-5 h-5 flex-shrink-0" />
                <span>
                  Please select at least one file to continue
                </span>
              </div>
            </Alert>
          )}

          <Button
            variant="primary"
            size="md"
            onClick={handleContinue}
            disabled={selectedFileIds.size === 0}
            className="w-full"
          >
            Import Selected Files ({selectedFileIds.size})
          </Button>
        </div>
      )}
    </div>
  );
}
