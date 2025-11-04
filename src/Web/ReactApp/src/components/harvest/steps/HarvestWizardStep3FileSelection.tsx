import React, { useEffect, useState } from 'react';
import { CheckCircle, AlertCircle, Loader } from 'lucide-react';
import type { HarvestOptions, HarvestDiscoveredFile } from '../HarvestWizard';

interface HarvestWizardStep3FileSelectionProps {
  printerId?: string;
  options?: HarvestOptions;
  files: HarvestDiscoveredFile[];
  isDiscovering: boolean;
  onComplete: (selectedFileIds: string[]) => void;
  onStartDiscovery: () => void;
}

export function HarvestWizardStep3FileSelection({
  files,
  isDiscovering,
  onComplete,
  onStartDiscovery,
}: HarvestWizardStep3FileSelectionProps) {
  const [selectedFileIds, setSelectedFileIds] = useState<Set<string>>(new Set());
  const [allSelected, setAllSelected] = useState(false);
  const [discoveryStarted, setDiscoveryStarted] = useState(false);

  // Auto-start discovery when component mounts
  useEffect(() => {
    if (!discoveryStarted) {
      onStartDiscovery();
      setDiscoveryStarted(true);
    }
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    // Auto-select all files when they appear
    if (files.length > 0 && selectedFileIds.size === 0) {
      const ids = new Set(files.map(f => f.id));
      setSelectedFileIds(ids);
      setAllSelected(true);
    }
  }, [files.length]); // eslint-disable-line react-hooks/exhaustive-deps

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
      // Complete step 3 and pass selected file IDs
      onComplete(Array.from(selectedFileIds));
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
          <div className="flex items-center gap-3 p-3 bg-pf-info-bg border border-pf-info rounded-lg">
            <Loader className="w-5 h-5 text-pf-info animate-spin" />
            <span className="text-sm text-pf-info">Scanning printer for G-code files...</span>
          </div>
        )}
      </div>

      {files.length > 0 && (
        <div className="space-y-3">
          <div className="flex items-center justify-between p-3 bg-pf-surface border border-pf-border rounded-lg">
            <label htmlFor="selectAll" className="flex items-center gap-3 cursor-pointer">
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
            <div className="flex items-center gap-2 p-3 bg-pf-success-bg border border-pf-success rounded-lg">
              <CheckCircle className="w-5 h-5 text-pf-success flex-shrink-0" />
              <span className="text-sm text-pf-success">
                {selectedFileIds.size} file{selectedFileIds.size !== 1 ? 's' : ''} selected for import
              </span>
            </div>
          )}

          {selectedFileIds.size === 0 && files.length > 0 && (
            <div className="flex items-center gap-2 p-3 bg-pf-warning-bg border border-pf-warning rounded-lg">
              <AlertCircle className="w-5 h-5 text-pf-warning flex-shrink-0" />
              <span className="text-sm text-pf-warning">
                Please select at least one file to continue
              </span>
            </div>
          )}

          <button
            onClick={handleContinue}
            disabled={selectedFileIds.size === 0}
            className="w-full px-4 py-2 bg-pf-accent text-white rounded-lg hover:bg-pf-accent-hover disabled:opacity-50 disabled:cursor-not-allowed transition-colors font-medium"
          >
            Import Selected Files ({selectedFileIds.size})
          </button>
        </div>
      )}
    </div>
  );
}
