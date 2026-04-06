import React, { useMemo, useState } from 'react';
import clsx from 'clsx';
import { Badge, Tabs } from '@/common/components/ui';

export interface CompatibilityMatrixProps {
  machines: Array<{ 
    id: string; 
    name: string; 
    nozzleDiameter?: number; 
    manufacturer?: string;
  }>;
  filaments: Array<{ 
    id: string; 
    name: string; 
    material?: string;
  }>;
  processes: Array<{ 
    id: string; 
    name: string; 
    layerHeight?: number;
  }>;
  compatibilityData?: Record<string, Record<string, boolean>>;
  onCellClick?: (machineId: string, profileId: string, profileType: 'filament' | 'process') => void;
  className?: string;
}

type MatrixMode = 'filament' | 'process';

function getCompatibilityStatus(
  machineId: string,
  profileId: string,
  compatibilityData?: Record<string, Record<string, boolean>>
): boolean | null {
  if (!compatibilityData) return null;
  const machineData = compatibilityData[machineId];
  if (!machineData) return null;
  if (profileId in machineData) {
    return machineData[profileId];
  }
  return null;
}

function getCompatibilityIcon(isCompatible: boolean | null): string {
  if (isCompatible === true) return '✓';
  if (isCompatible === false) return '✗';
  return '?';
}

function getCompatibilityColor(isCompatible: boolean | null): string {
  if (isCompatible === true) return 'text-pf-success bg-pf-success/10';
  if (isCompatible === false) return 'text-pf-error bg-pf-error/10';
  return 'text-pf-text-tertiary bg-pf-bg-2';
}

export function CompatibilityMatrix({
  machines,
  filaments,
  processes,
  compatibilityData,
  onCellClick,
  className,
}: CompatibilityMatrixProps) {
  const [mode, setMode] = useState<MatrixMode>('filament');

  const groupedMachines = useMemo(() => {
    const groups = new Map<string, typeof machines>();
    
    for (const machine of machines) {
      const manufacturer = machine.manufacturer || 'Other';
      if (!groups.has(manufacturer)) {
        groups.set(manufacturer, []);
      }
      groups.get(manufacturer)!.push(machine);
    }

    return Array.from(groups.entries()).sort(([a], [b]) => {
      if (a === 'Other') return 1;
      if (b === 'Other') return -1;
      return a.localeCompare(b);
    });
  }, [machines]);

  const currentProfiles = mode === 'filament' ? filaments : processes;

  const compatibilityStats = useMemo(() => {
    let compatible = 0;
    let incompatible = 0;
    let unknown = 0;

    for (const machine of machines) {
      for (const profile of currentProfiles) {
        const status = getCompatibilityStatus(machine.id, profile.id, compatibilityData);
        if (status === true) compatible++;
        else if (status === false) incompatible++;
        else unknown++;
      }
    }

    return { compatible, incompatible, unknown };
  }, [machines, currentProfiles, compatibilityData]);

  if (machines.length === 0 || (filaments.length === 0 && processes.length === 0)) {
    return (
      <div className={clsx('rounded-lg border border-pf-border bg-pf-bg-0 p-8', className)}>
        <div className="text-center text-pf-text-secondary">
          <p className="text-lg font-medium">No data available</p>
          <p className="mt-2 text-sm">Add machines and profiles to view compatibility matrix</p>
        </div>
      </div>
    );
  }

  return (
    <div className={clsx('flex flex-col gap-4', className)}>
      {/* Header with mode toggle and stats */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <h3 className="text-lg font-medium text-pf-text-primary">Compatibility Matrix</h3>
          <div className="flex items-center gap-2">
            <Badge variant="success" size="sm">
              {compatibilityStats.compatible} compatible
            </Badge>
            <Badge variant="error" size="sm">
              {compatibilityStats.incompatible} incompatible
            </Badge>
            <Badge variant="default" size="sm">
              {compatibilityStats.unknown} unknown
            </Badge>
          </div>
        </div>
      </div>

      {/* Mode tabs */}
      <Tabs defaultTab="filament" activeTab={mode} onTabChange={(tab) => setMode(tab as MatrixMode)}>
        <Tabs.List>
          <Tabs.Tab id="filament">Machine × Filament</Tabs.Tab>
          <Tabs.Tab id="process">Machine × Process</Tabs.Tab>
        </Tabs.List>
      </Tabs>

      {/* Matrix table */}
      <div className="overflow-x-auto rounded-lg border border-pf-border">
        <table className="w-full border-collapse">
          <thead>
            <tr className="border-b border-pf-border bg-pf-bg-1">
              <th className="sticky left-0 z-10 min-w-[200px] border-r border-pf-border bg-pf-bg-1 px-4 py-3 text-left text-sm font-medium text-pf-text-secondary">
                Machine
              </th>
              {currentProfiles.map((profile) => (
                <th
                  key={profile.id}
                  className="min-w-[120px] px-3 py-3 text-center text-xs font-medium text-pf-text-secondary"
                >
                  <div className="flex flex-col gap-1">
                    <span className="truncate">{profile.name}</span>
                    {mode === 'filament' && 'material' in profile && profile.material && (
                      <span className="text-xs text-pf-text-tertiary">{profile.material}</span>
                    )}
                    {mode === 'process' && 'layerHeight' in profile && profile.layerHeight && (
                      <span className="text-xs text-pf-text-tertiary">{profile.layerHeight}mm</span>
                    )}
                  </div>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {groupedMachines.map(([manufacturer, machineList]) => (
              <React.Fragment key={manufacturer}>
                {/* Manufacturer header row */}
                <tr className="border-b border-pf-border bg-pf-bg-2">
                  <td
                    colSpan={currentProfiles.length + 1}
                    className="px-4 py-2 text-sm font-semibold uppercase tracking-wide text-pf-text-secondary"
                  >
                    {manufacturer}
                  </td>
                </tr>
                {/* Machine rows */}
                {machineList.map((machine) => (
                  <tr
                    key={machine.id}
                    className="border-b border-pf-border last:border-b-0 hover:bg-pf-bg-1/50"
                  >
                    <td className="sticky left-0 z-10 border-r border-pf-border bg-pf-bg-0 px-4 py-3 text-sm font-medium text-pf-text-primary">
                      <div className="flex flex-col gap-1">
                        <span>{machine.name}</span>
                        {machine.nozzleDiameter && (
                          <span className="text-xs text-pf-text-tertiary">
                            {machine.nozzleDiameter}mm nozzle
                          </span>
                        )}
                      </div>
                    </td>
                    {currentProfiles.map((profile) => {
                      const isCompatible = getCompatibilityStatus(
                        machine.id,
                        profile.id,
                        compatibilityData
                      );
                      const icon = getCompatibilityIcon(isCompatible);
                      const colorClass = getCompatibilityColor(isCompatible);

                      return (
                        <td
                          key={profile.id}
                          className="px-3 py-3 text-center"
                        >
                          <button
                            type="button"
                            onClick={() => onCellClick?.(machine.id, profile.id, mode)}
                            className={clsx(
                              'inline-flex h-8 w-8 items-center justify-center rounded-md text-sm font-medium transition-colors',
                              colorClass,
                              onCellClick && 'cursor-pointer hover:opacity-80',
                              !onCellClick && 'cursor-default'
                            )}
                            title={
                              isCompatible === true
                                ? 'Compatible'
                                : isCompatible === false
                                ? 'Incompatible'
                                : 'Unknown compatibility'
                            }
                          >
                            {icon}
                          </button>
                        </td>
                      );
                    })}
                  </tr>
                ))}
              </React.Fragment>
            ))}
          </tbody>
        </table>
      </div>

      {/* Legend */}
      <div className="flex items-center gap-4 text-sm text-pf-text-secondary">
        <span className="font-medium">Legend:</span>
        <div className="flex items-center gap-1">
          <span className="text-pf-success">✓</span>
          <span>Compatible</span>
        </div>
        <div className="flex items-center gap-1">
          <span className="text-pf-error">✗</span>
          <span>Incompatible</span>
        </div>
        <div className="flex items-center gap-1">
          <span className="text-pf-text-tertiary">?</span>
          <span>Unknown</span>
        </div>
        {onCellClick && (
          <span className="ml-auto text-xs italic">Click cells to toggle compatibility</span>
        )}
      </div>
    </div>
  );
}
