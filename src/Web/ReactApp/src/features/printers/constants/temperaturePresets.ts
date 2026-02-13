import type { TempTargets } from '@/types/api';

export interface MaterialTemperaturePreset {
  label: string;
  value: string;
  hotend: number;
  bed: number;
}

export const materialPresets: MaterialTemperaturePreset[] = [
  { label: 'ABS', value: 'ABS', hotend: 250, bed: 100 },
  { label: 'PLA', value: 'PLA', hotend: 210, bed: 60 },
  { label: 'PC', value: 'PC', hotend: 280, bed: 110 },
  { label: 'PETG', value: 'PETG', hotend: 230, bed: 75 },
  { label: 'Cooldown', value: 'Cooldown', hotend: 0, bed: 0 },
];

export const hotendPresetOptions = [...materialPresets]
  .sort((a, b) => b.hotend - a.hotend)
  .map((preset) => ({
    value: preset.value,
    label: `${preset.hotend}°C`,
  }));

export const bedPresetOptions = [...materialPresets]
  .sort((a, b) => b.bed - a.bed)
  .map((preset) => ({
    value: preset.value,
    label: `${preset.bed}°C`,
  }));

export function getPresetTargets(preset: string): TempTargets | null {
  const match = materialPresets.find((candidate) => candidate.value.toLowerCase() === preset.toLowerCase());
  if (!match) {
    return null;
  }

  return { hotend: match.hotend, bed: match.bed };
}
