import React from 'react';
import { Select } from '@/common/components/ui';
import type { MaterialType } from '@/types/slicer';
import type { FilamentProfileListItem } from './types';
import { MATERIAL_PRESETS } from './types';

interface FilamentProfileSelectorProps {
  /** Available filament profiles from slicer (may be empty) */
  availableFilamentProfiles: FilamentProfileListItem[];
  /** Selected filament profile ID (when using slicer profiles) */
  selectedFilamentProfileId: string;
  /** Selected material type (when using presets) */
  selectedFilamentMaterial: MaterialType;
  /** Whether a printer model has been selected */
  hasPrinterModelSelected: boolean;
  /** Callback when filament profile changes */
  onFilamentProfileChange: (profileId: string) => void;
  /** Callback when material preset changes */
  onMaterialChange: (material: MaterialType) => void;
  /** Optional CSS class name */
  className?: string;
}

/**
 * Filament profile selection component.
 * Shows slicer profiles if available, otherwise falls back to material presets.
 */
export const FilamentProfileSelector: React.FC<FilamentProfileSelectorProps> = ({
  availableFilamentProfiles,
  selectedFilamentProfileId,
  selectedFilamentMaterial,
  hasPrinterModelSelected,
  onFilamentProfileChange,
  onMaterialChange,
  className
}) => {
  const hasSlicerProfiles = availableFilamentProfiles.length > 0;

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-lg p-4 ${className ?? ''}`}>
      <label className="block text-sm font-semibold text-pf-text-primary mb-2">Filament Profile</label>
      {hasSlicerProfiles ? (
        <Select
          value={selectedFilamentProfileId}
          onChange={e => onFilamentProfileChange(e.target.value)}
          className="w-full"
        >
          <option value="">-- Select Filament Profile --</option>
          {availableFilamentProfiles.map(profile => (
            <option key={profile.id} value={profile.id}>
              {profile.name} ({profile.material})
            </option>
          ))}
        </Select>
      ) : (
        <>
          <Select
            value={selectedFilamentMaterial}
            onChange={e => onMaterialChange(e.target.value as MaterialType)}
            className="w-full"
          >
            {Object.keys(MATERIAL_PRESETS).map(m => (
              <option key={m} value={m}>{m}</option>
            ))}
          </Select>
          <div className="text-xs text-pf-text-muted mt-2">
            {MATERIAL_PRESETS[selectedFilamentMaterial].nozzleTemp}°C nozzle, {MATERIAL_PRESETS[selectedFilamentMaterial].bedTemp}°C bed
          </div>
          {hasPrinterModelSelected && (
            <p className="text-xs text-pf-warning mt-1">No filament profiles for this model - using presets</p>
          )}
        </>
      )}
    </div>
  );
};

export default FilamentProfileSelector;
