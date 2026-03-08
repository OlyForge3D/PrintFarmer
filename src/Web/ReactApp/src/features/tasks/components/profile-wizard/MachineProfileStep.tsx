import React, { useMemo } from 'react';
import { Button, Alert, Checkbox } from '@/common/components/ui';
import { AlertCircleIcon } from '@/common/components/icons/MdiIcons';
import { Check, ChevronRight, ChevronLeft, CheckCircle } from 'lucide-react';
import type { MachineProfileDto } from './types';

interface MachineProfileStepProps {
  machineProfiles: MachineProfileDto[];
  selectedMachines: Set<string>;
  onToggleMachine: (name: string) => void;
  onSelectAll: () => void;
  onSelectNone: () => void;
  onNext: () => void;
  /** Optional back handler - shown when wizard has model selection step */
  onBack?: () => void;
  isLoading?: boolean;
  error?: Error | null;
  printerModelName?: string;
  /** Names of profiles already imported for this model (shown with badge) */
  importedMachineNames?: string[];
}

export const MachineProfileStep: React.FC<MachineProfileStepProps> = ({
  machineProfiles,
  selectedMachines,
  onToggleMachine,
  onSelectAll,
  onSelectNone,
  onNext,
  onBack,
  isLoading,
  error,
  printerModelName,
  importedMachineNames,
}) => {
  // Create a set for fast lookup
  const importedSet = useMemo(
    () => new Set(importedMachineNames || []),
    [importedMachineNames]
  );

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent" />
      </div>
    );
  }

  if (error) {
    return (
      <Alert className="mb-4">
        <AlertCircleIcon className="h-5 w-5" />
        <span>Failed to load machine profiles. Make sure the OrcaSlicer worker is running.</span>
      </Alert>
    );
  }

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
      <div className="flex items-center justify-between mb-4">
        <div>
          <h3 className="text-lg font-semibold text-pf-text-primary">
            Select Machine Profile(s)
          </h3>
          <p className="text-sm text-pf-text-secondary">
            Choose the nozzle configuration(s) for your {printerModelName}
          </p>
        </div>
        <div className="flex gap-2">
          <Button size="sm" onClick={onSelectAll}>All</Button>
          <Button size="sm" onClick={onSelectNone}>None</Button>
        </div>
      </div>

      {machineProfiles.length === 0 ? (
        <Alert>
          <AlertCircleIcon className="h-5 w-5" />
          <span>
            No machine profiles found for &quot;{printerModelName}&quot;.
            The name may not match OrcaSlicer&apos;s naming convention exactly.
          </span>
        </Alert>
      ) : (
        <div className="space-y-2 max-h-[400px] overflow-y-auto">
          {machineProfiles.map((machine) => {
            const isSelected = selectedMachines.has(machine.name);
            const isImported = importedSet.has(machine.name);
            return (
              <label
                key={machine.name}
                className={`flex items-center gap-3 p-4 rounded-lg border cursor-pointer transition-colors ${
                  isSelected
                    ? 'border-pf-accent bg-pf-accent/10'
                    : 'border-pf-border hover:bg-pf-bg-hover'
                }`}
              >
                <Checkbox
                  checked={isSelected}
                  onChange={() => onToggleMachine(machine.name)}
                />
                <div className="flex-1">
                  <div className="flex items-center gap-2">
                    <span className="font-medium text-pf-text-primary">{machine.name}</span>
                    {isImported && (
                      <span className="inline-flex items-center gap-1 px-2 py-0.5 text-xs font-medium rounded-full bg-pf-success/10 text-pf-success">
                        <CheckCircle className="h-3 w-3" />
                        Imported
                      </span>
                    )}
                  </div>
                  {machine.nozzleDiameter && (
                    <div className="text-sm text-pf-text-secondary">
                      {machine.nozzleDiameter}mm nozzle
                    </div>
                  )}
                </div>
                {isSelected && <Check className="h-5 w-5 text-pf-accent" />}
              </label>
            );
          })}
        </div>
      )}

      <div className="mt-6 flex justify-between items-center">
        <div className="flex items-center gap-4">
          {onBack && (
            <Button
              variant="secondary"
              onClick={onBack}
              iconLeft={<ChevronLeft className="h-4 w-4 rotate-180" />} 
              className="flex items-center gap-2"
            >
              Back
            </Button>
          )}
          <div className="text-sm text-pf-text-secondary">
            {selectedMachines.size} of {machineProfiles.length} selected
            {importedSet.size > 0 && ` (${importedSet.size} already imported)`}
          </div>
        </div>
        <Button
          onClick={onNext}
          disabled={selectedMachines.size === 0}
          iconRight={<ChevronRight className="h-4 w-4" />}
        >
          Next
        </Button>
      </div>
    </div>
  );
};
