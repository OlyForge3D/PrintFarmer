import { FormField, Select } from '@/common/components/ui';
import { BED_TYPE_OPTIONS } from '@/features/slicer/components/settings/metadataTypes';
import type { FilamentBedContext } from '@/features/slicer/utils/bedSurfaceTemperature';

interface BedSurfaceSettingsProps {
  id: string;
  selectedBedType: string;
  onChange: (value: string) => void;
  context: FilamentBedContext;
}

export function BedSurfaceSettings({
  id, selectedBedType, onChange, context,
}: BedSurfaceSettingsProps) {
  if (context.supportMultiBedTypes !== true) return null;
  const { bedType } = context;
  return (
    <FormField label="Bed Type" htmlFor={id}>
      <Select id={id} value={selectedBedType} onChange={event => onChange(event.target.value)}>
        <option value="">Default{bedType && !selectedBedType ? ` (${bedType})` : ''}</option>
        {BED_TYPE_OPTIONS.map(option => (
          <option key={option.value} value={option.value}>{option.label}</option>
        ))}
      </Select>
    </FormField>
  );
}
