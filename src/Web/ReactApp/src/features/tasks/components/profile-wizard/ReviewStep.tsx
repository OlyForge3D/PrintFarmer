import React, { useMemo } from 'react';
import { Button } from '@/common/components/ui';
import { CheckCircleIcon } from '@/common/components/icons/MdiIcons';
import { Download, Printer, Palette, CheckCircle, ChevronLeft } from 'lucide-react';
import type { ProcessProfileDto } from './types';

interface ReviewStepProps {
  /** Selected machine profile names */
  selectedMachines: Set<string>;
  /** Process profiles (auto-imported) */
  processProfiles: ProcessProfileDto[];
  /** Selected filament profile names */
  selectedFilaments: Set<string>;
  /** Go back to previous step */
  onBack: () => void;
  /** Execute the import */
  onImport: () => void;
  /** Whether import is in progress */
  isImporting: boolean;
  /** Names of machine profiles already imported */
  importedMachineNames?: string[];
  /** Names of process profiles already imported */
  importedProcessNames?: string[];
  /** Names of filament profiles already imported */
  importedFilamentNames?: string[];
}

export const ReviewStep: React.FC<ReviewStepProps> = ({
  selectedMachines,
  processProfiles,
  selectedFilaments,
  onBack,
  onImport,
  isImporting,
  importedMachineNames,
  importedProcessNames,
  importedFilamentNames,
}) => {
  // Create sets for fast lookup
  const importedMachineSet = useMemo(() => new Set(importedMachineNames || []), [importedMachineNames]);
  const importedProcessSet = useMemo(() => new Set(importedProcessNames || []), [importedProcessNames]);
  const importedFilamentSet = useMemo(() => new Set(importedFilamentNames || []), [importedFilamentNames]);

  // Count how many selected profiles are already imported
  const importedMachineCount = useMemo(
    () => Array.from(selectedMachines).filter((name) => importedMachineSet.has(name)).length,
    [selectedMachines, importedMachineSet]
  );
  const importedProcessCount = useMemo(
    () => processProfiles.filter((p) => importedProcessSet.has(p.name)).length,
    [processProfiles, importedProcessSet]
  );
  const importedFilamentCount = useMemo(
    () => Array.from(selectedFilaments).filter((name) => importedFilamentSet.has(name)).length,
    [selectedFilaments, importedFilamentSet]
  );

  const totalProfiles = selectedMachines.size + processProfiles.length + selectedFilaments.size;
  const totalAlreadyImported = importedMachineCount + importedProcessCount + importedFilamentCount;

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
      <h3 className="text-lg font-semibold text-pf-text-primary mb-4">Review &amp; Import</h3>

      <div className="space-y-4">
        {/* Machine Profiles */}
        <div className="p-4 border border-pf-border rounded-lg">
          <div className="flex items-center gap-2 mb-2">
            <Printer className="h-5 w-5 text-pf-accent" />
            <h4 className="font-medium text-pf-text-primary">Machine Profiles</h4>
            <span className="text-sm text-pf-text-tertiary">({selectedMachines.size} selected)</span>
            {importedMachineCount > 0 && (
              <span className="text-xs text-pf-success flex items-center gap-1">
                <CheckCircle className="h-3 w-3" />
                {importedMachineCount} already imported
              </span>
            )}
          </div>
          <div className="pl-7 flex flex-wrap gap-1">
            {Array.from(selectedMachines)
              .slice(0, 5)
              .map((name) => (
                <span
                  key={name}
                  className={`text-xs px-2 py-1 rounded ${
                    importedMachineSet.has(name)
                      ? 'bg-pf-success/10 text-pf-success'
                      : 'bg-pf-bg-2'
                  }`}
                >
                  {name}
                </span>
              ))}
            {selectedMachines.size > 5 && (
              <span className="text-xs text-pf-text-tertiary">+{selectedMachines.size - 5} more</span>
            )}
          </div>
        </div>

        {/* Process Profiles - Auto-imported */}
        <div className="p-4 border border-pf-border rounded-lg bg-pf-bg-2/50">
          <div className="flex items-center gap-2 mb-2">
            <CheckCircleIcon className="h-5 w-5 text-pf-success" />
            <h4 className="font-medium text-pf-text-primary">Process Profiles</h4>
            <span className="text-sm text-pf-text-tertiary">({processProfiles.length} auto-included)</span>
            {importedProcessCount > 0 && (
              <span className="text-xs text-pf-success flex items-center gap-1">
                <CheckCircle className="h-3 w-3" />
                {importedProcessCount} already imported
              </span>
            )}
          </div>
          <p className="text-sm text-pf-text-secondary pl-7">
            All compatible process profiles will be automatically imported for your selected machine(s).
          </p>
        </div>

        {/* Filament Profiles */}
        <div className="p-4 border border-pf-border rounded-lg">
          <div className="flex items-center gap-2 mb-2">
            <Palette className="h-5 w-5 text-pf-accent" />
            <h4 className="font-medium text-pf-text-primary">Filament Profiles</h4>
            <span className="text-sm text-pf-text-tertiary">({selectedFilaments.size} selected)</span>
            {importedFilamentCount > 0 && (
              <span className="text-xs text-pf-success flex items-center gap-1">
                <CheckCircle className="h-3 w-3" />
                {importedFilamentCount} already imported
              </span>
            )}
          </div>
          {selectedFilaments.size > 0 ? (
            <div className="pl-7 flex flex-wrap gap-1 max-h-24 overflow-y-auto">
              {Array.from(selectedFilaments)
                .slice(0, 15)
                .map((name) => (
                  <span
                    key={name}
                    className={`text-xs px-2 py-1 rounded ${
                      importedFilamentSet.has(name)
                        ? 'bg-pf-success/10 text-pf-success'
                        : 'bg-pf-bg-2'
                    }`}
                  >
                    {name}
                  </span>
                ))}
              {selectedFilaments.size > 15 && (
                <span className="text-xs text-pf-text-tertiary">+{selectedFilaments.size - 15} more</span>
              )}
            </div>
          ) : (
            <p className="text-sm text-pf-text-tertiary pl-7 italic">No filament profiles selected</p>
          )}
        </div>
      </div>

      {/* Summary */}
      <div className="mt-6 p-4 bg-pf-accent/10 rounded-lg">
        <div className="flex items-center gap-2 text-pf-accent">
          <CheckCircleIcon className="h-5 w-5" />
          <span className="font-medium">
            Ready to import {totalProfiles} profiles
            {totalAlreadyImported > 0 && (
              <span className="text-pf-text-secondary font-normal">
                {' '}({totalAlreadyImported} already imported, will be skipped as duplicates)
              </span>
            )}
          </span>
        </div>
      </div>

      {/* Navigation */}
      <div className="mt-6 flex justify-between">
        <Button 
          onClick={onBack}
          iconLeft={<ChevronLeft className="h-4 w-4 rotate-180" />}
          >Back</Button>
        <Button
          onClick={onImport}
          disabled={isImporting || selectedMachines.size === 0}
          className="flex items-center gap-2"
          iconRight={<Download className="h-4 w-4" />}
        >
        {isImporting ? 'Importing...' : 'Import'}
        </Button>
      </div>
    </div>
  );
};
