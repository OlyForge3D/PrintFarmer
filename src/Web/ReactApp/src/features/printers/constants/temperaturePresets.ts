import type { PrinterCapabilitiesDto, PrinterSensor, TempTargets } from '@/types/api';
import { primaryHeaterLimits } from '@/features/printers/utils/additionalPrinterSensors';

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

export function temperatureTargetLimitError(
  targets: Partial<TempTargets>,
  sensors: PrinterSensor[] | undefined,
  capabilities: Pick<PrinterCapabilitiesDto, 'maxHotendTemp' | 'maxBedTemp'> | undefined,
): string | undefined {
  for (const [heater, label, persistedMax] of [
    ['hotend', 'Hotend', capabilities?.maxHotendTemp],
    ['bed', 'Bed', capabilities?.maxBedTemp],
  ] as const) {
    const target = targets[heater];
    const { max } = primaryHeaterLimits(sensors, heater, persistedMax);
    if (target !== undefined && max !== undefined && target > max) {
      return `${label} target ${target}°C exceeds maximum ${max}°C. Nothing sent.`;
    }
  }
  return undefined;
}

/** Default minimum hotend temperature (°C) for extrusion when material is unknown. Matches PLA. */
export const DEFAULT_EXTRUDE_MIN_TEMP = 210;

/** Selectable extrusion distances in mm. */
export const EXTRUDE_DISTANCE_OPTIONS = [10, 25, 50, 100] as const;

/** Default extrusion distance in mm. */
export const DEFAULT_EXTRUDE_DISTANCE_MM = 10;

/** Selectable extrusion speeds in mm/s. */
export const EXTRUDE_SPEED_OPTIONS = [1, 5, 10] as const;

/** Default extrusion speed in mm/s. */
export const DEFAULT_EXTRUDE_SPEED_MMS = 5;

/**
 * Returns the minimum hotend temperature required before extruding for a given material.
 * Falls back to DEFAULT_EXTRUDE_MIN_TEMP (210 °C / PLA) when the material is unknown.
 */
export function getExtrudeMinTemp(material?: string): number {
  if (material) {
    const targets = getPresetTargets(material);
    if (targets && targets.hotend > 0) return targets.hotend;
  }
  return DEFAULT_EXTRUDE_MIN_TEMP;
}
