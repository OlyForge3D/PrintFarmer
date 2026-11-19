import { FormField, Select } from '@/common/components/ui';
import { BED_TYPE_OPTIONS } from '@/features/slicer/components/settings/metadataTypes';
import { formatBedTemperatures, getBedSurfaceTemperatures } from '@/features/slicer/utils/bedSurfaceTemperature';
import type { FilamentBedContext } from '@/features/slicer/utils/bedSurfaceTemperature';

interface BedSurfaceSettingsProps {
  id: string;
  selectedBedType: string;
  onChange: (value: string) => void;
  context: FilamentBedContext;
  filaments: Array<{ name: string; settings?: Record<string, unknown> } | undefined>;
}

export function BedSurfaceSettings({
  id, selectedBedType, onChange, context, filaments,
}: BedSurfaceSettingsProps) {
  const { bedType } = context;
  return (
    <div className="space-y-2">
      {context.supportMultiBedTypes !== false && (
        <FormField label="Bed Type" htmlFor={id}>
          <Select id={id} value={selectedBedType} onChange={event => onChange(event.target.value)}>
            <option value="">Inherit from profile{bedType && !selectedBedType ? ` (${bedType})` : ''}</option>
            {BED_TYPE_OPTIONS.map(option => (
              <option key={option.value} value={option.value}>{option.label}</option>
            ))}
          </Select>
        </FormField>
      )}
      <div className="space-y-1 text-sm text-pf-text-secondary" aria-live="polite">
        {!bedType && <p>Bed surface unknown until resolved by OrcaSlicer.</p>}
        {filaments.map((filament, index) => {
          const temperatures = getBedSurfaceTemperatures(bedType, filament?.settings);
          const hasZero = [...temperatures.firstLayer, ...temperatures.otherLayers].includes(0);
          return (
            <div key={`${filament?.name ?? 'unselected'}-${index}`}>
              {filaments.length > 1 && <p>Extruder {index + 1}: {filament?.name ?? 'No filament selected'}</p>}
              <p className="font-pf-mono tabular-nums">
                Bed first layer: {formatBedTemperatures(temperatures.firstLayer)};
                {' '}other layers: {formatBedTemperatures(temperatures.otherLayers)}
              </p>
              {hasZero && (
                <p className="text-pf-warning">
                  Profile specifies 0°C. Check that this material supports the selected plate.
                </p>
              )}
            </div>
          );
        })}
        <p>
          Profile values only. OrcaSlicer applies the bed-temperature formula, active filaments and start G-code;
          unknown values are not replaced.
        </p>
      </div>
    </div>
  );
}
