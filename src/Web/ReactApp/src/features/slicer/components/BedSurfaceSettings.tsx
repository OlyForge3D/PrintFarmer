import { FormField, Select } from '@/common/components/ui';
import { BED_TYPE_OPTIONS } from '@/features/slicer/components/settings/metadataTypes';
import {
  formatBedTemperatures,
  getBedSurfaceTemperatures,
  resolveBedType,
  getFilamentBedContext,
} from '@/features/slicer/utils/bedSurfaceTemperature';

interface BedSurfaceSettingsProps {
  id: string;
  selectedBedType: string;
  onChange: (value: string) => void;
  machineSettings?: Record<string, unknown>;
  manufacturer?: string;
  processSettings?: Record<string, unknown>;
  filaments: Array<{ name: string; settings?: Record<string, unknown> } | undefined>;
}

export function BedSurfaceSettings({
  id, selectedBedType, onChange, machineSettings, processSettings, filaments, manufacturer,
}: BedSurfaceSettingsProps) {
  const bedType = resolveBedType(selectedBedType, processSettings, machineSettings);
  const context = getFilamentBedContext(machineSettings, processSettings, selectedBedType, manufacturer);
  return (
    <div className="space-y-2">
      <FormField label="Bed Type" htmlFor={id}>
        <Select id={id} value={selectedBedType} onChange={event => onChange(event.target.value)}>
          <option value="">Inherit from profile{bedType && !selectedBedType ? ` (${bedType})` : ''}</option>
          {BED_TYPE_OPTIONS.map(option => (
            <option key={option.value} value={option.value}>{option.label}</option>
          ))}
        </Select>
      </FormField>
      <div className="space-y-1 text-sm text-pf-text-secondary" aria-live="polite">
        {context.supportMultiBedTypes === false && <p>Machine profile has support for multiple bed types disabled.</p>}
        {!bedType && <p>Bed surface unknown until resolved by OrcaSlicer.</p>}
        {filaments.map((filament, index) => {
          const temps = getBedSurfaceTemperatures(bedType, filament?.settings);
          const hasZero = [...temps.firstLayer, ...temps.otherLayers].includes(0);
          return (
            <div key={index}>
              {filaments.length > 1 && <p>Extruder {index + 1}: {filament?.name ?? 'No filament selected'}</p>}
              <p>Bed first layer: {formatBedTemperatures(temps.firstLayer)}; other layers: {formatBedTemperatures(temps.otherLayers)}</p>
              {hasZero && <p className="text-pf-warning">Profile specifies 0°C. Check that this material supports the selected plate.</p>}
            </div>
          );
        })}
        <p>Profile values only. OrcaSlicer applies the bed-temperature formula, active filaments and start G-code; unknown values are not replaced.</p>
      </div>
    </div>
  );
}
