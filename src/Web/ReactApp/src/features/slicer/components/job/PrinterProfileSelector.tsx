import React from 'react';
import { Select } from '@/common/components/ui';
import type { PrinterModelOption, MachineProfileListItem } from './types';

interface PrinterProfileSelectorProps {
  /** Available manufacturers from profile hierarchy */
  availableManufacturers: string[];
  /** Available printer models for selected manufacturer */
  availablePrinterModels: PrinterModelOption[];
  /** Available machine profiles for selected model */
  availableMachineProfiles: MachineProfileListItem[];
  /** Currently selected manufacturer */
  selectedManufacturer: string;
  /** Currently selected printer model key */
  selectedPrinterModel: string;
  /** Currently selected machine profile ID */
  selectedMachineProfileId: string;
  /** Callback when manufacturer changes */
  onManufacturerChange: (manufacturer: string) => void;
  /** Callback when printer model changes */
  onPrinterModelChange: (modelKey: string) => void;
  /** Callback when machine profile changes */
  onMachineProfileChange: (profileId: string) => void;
  /** Optional CSS class name */
  className?: string;
}

/**
 * Cascading printer profile selection component.
 * Flow: Manufacturer → Printer Model → Machine Profile (nozzle variant)
 */
export const PrinterProfileSelector: React.FC<PrinterProfileSelectorProps> = ({
  availableManufacturers,
  availablePrinterModels,
  availableMachineProfiles,
  selectedManufacturer,
  selectedPrinterModel,
  selectedMachineProfileId,
  onManufacturerChange,
  onPrinterModelChange,
  onMachineProfileChange,
  className
}) => {
  const fieldIdPrefix = React.useId();
  const manufacturerSelectId = `${fieldIdPrefix}-manufacturer`;
  const printerModelSelectId = `${fieldIdPrefix}-printer-model`;
  const machineProfileSelectId = `${fieldIdPrefix}-machine-profile`;

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-lg p-4 space-y-3 ${className ?? ''}`}>
      <label className="block text-sm font-semibold text-pf-text-primary">Printer Profile</label>
      
      {/* Manufacturer Selection */}
      <div>
        <label htmlFor={manufacturerSelectId} className="block text-xs text-pf-text-muted mb-1">Manufacturer</label>
        <Select
          id={manufacturerSelectId}
          value={selectedManufacturer}
          onChange={e => onManufacturerChange(e.target.value)}
          className="w-full"
        >
          <option value="">-- Select Manufacturer --</option>
          {availableManufacturers.map(mfg => (
            <option key={mfg} value={mfg}>{mfg}</option>
          ))}
        </Select>
      </div>

      {/* Printer Model Selection */}
      <div>
        <label htmlFor={printerModelSelectId} className="block text-xs text-pf-text-muted mb-1">Printer Model</label>
        <Select
          id={printerModelSelectId}
          value={selectedPrinterModel}
          onChange={e => onPrinterModelChange(e.target.value)}
          disabled={!selectedManufacturer}
          className={`w-full ${!selectedManufacturer ? 'opacity-50' : ''}`}
        >
          <option value="">-- Select Printer Model --</option>
          {availablePrinterModels.map(model => (
            <option key={model.key} value={model.key}>{model.name}</option>
          ))}
        </Select>
      </div>

      {/* Machine Profile Selection (nozzle variants) */}
      <div>
        <label htmlFor={machineProfileSelectId} className="block text-xs text-pf-text-muted mb-1">Machine Profile</label>
        <Select
          id={machineProfileSelectId}
          value={selectedMachineProfileId}
          onChange={e => onMachineProfileChange(e.target.value)}
          disabled={!selectedPrinterModel || availableMachineProfiles.length === 0}
          className={`w-full ${!selectedPrinterModel ? 'opacity-50' : ''}`}
        >
          <option value="">-- Select Machine Profile --</option>
          {availableMachineProfiles.map(profile => (
            <option key={profile.id} value={profile.id}>
              {profile.name}
              {profile.nozzleDiameter ? ` (${profile.nozzleDiameter}mm)` : ''}
            </option>
          ))}
        </Select>
        {selectedPrinterModel && availableMachineProfiles.length === 0 && (
          <p className="text-xs text-pf-warning mt-1">No machine profiles available for this model</p>
        )}
      </div>
    </div>
  );
};

export default PrinterProfileSelector;
