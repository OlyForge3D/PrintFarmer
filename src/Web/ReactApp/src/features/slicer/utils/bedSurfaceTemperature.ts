// Native OrcaSlicer plate names -> filament keys (PrintConfig.hpp get_bed_temp_key).
const BED_TEMPERATURE_KEYS = new Map([
  ['Cool Plate', 'cool_plate_temp'],
  ['Engineering Plate', 'eng_plate_temp'],
  ['High Temp Plate', 'hot_plate_temp'],
  ['Textured PEI Plate', 'textured_plate_temp'],
  ['Textured Cool Plate', 'textured_cool_plate_temp'],
  ['Supertack Plate', 'supertack_plate_temp'],
]);

type Settings = Record<string, unknown> | undefined;

export interface FilamentBedContext {
  bedType?: string;
  supportMultiBedTypes?: boolean;
}

function readNativeBoolean(value: unknown): boolean | undefined {
  if (value === true || value === 'true' || value === '1' || value === 1) return true;
  if (value === false || value === 'false' || value === '0' || value === 0) return false;
  return undefined;
}

export function getFilamentBedContext(
  machine: Settings, process: Settings, selectedBedType: string, manufacturer?: string,
): FilamentBedContext {
  const vendor = manufacturer?.trim().toLowerCase();
  const model = machine?.printer_model;
  const isBambu = ['bbl', 'bambu', 'bambu lab', 'bambulab'].includes(vendor ?? '')
    || (!vendor && typeof model === 'string' && model.trimStart().toLowerCase().startsWith('bambu lab'));
  return {
    bedType: resolveBedType(selectedBedType, process, machine),
    supportMultiBedTypes: isBambu || readNativeBoolean(machine?.support_multi_bed_types),
  };
}

/** Orca hides other plate rows only for a known single-bed machine and known active plate. */
export function getSingleBedTemperatureKey(context: FilamentBedContext | undefined): string | undefined {
  return context?.supportMultiBedTypes === false && context.bedType
    ? BED_TEMPERATURE_KEYS.get(context.bedType)
    : undefined;
}

export function isBedTemperatureSettingVisible(key: string, context: FilamentBedContext | undefined): boolean {
  const activeKey = getSingleBedTemperatureKey(context);
  if (!activeKey) return true;
  const isBedKey = Array.from(BED_TEMPERATURE_KEYS.values()).some(base => key === base || key === `${base}_initial_layer`);
  return !isBedKey || key === activeKey || key === `${activeKey}_initial_layer`;
}

/** Read a scalar without guessing a default or collapsing multi-value settings. */
export function readBedType(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value : undefined;
}

export function resolveBedType(selectedBedType: string, process: Settings, machine: Settings): string | undefined {
  return selectedBedType || readBedType(process?.curr_bed_type) || readBedType(machine?.curr_bed_type);
}

function temperatures(value: unknown): Array<number | null> {
  const values: unknown[] = Array.isArray(value) ? value : [value];
  return values.length === 0 ? [null] : values.map(item => {
    if (typeof item !== 'number' && (typeof item !== 'string' || !/^\d+(?:\.\d+)?$/.test(item.trim()))) {
      return null;
    }
    const number = Number(item);
    return Number.isFinite(number) && number >= 0 ? number : null;
  });
}

/** Per-filament profile values only, not a prediction of start-G-code heating. */
export function getBedSurfaceTemperatures(bedType: string | undefined, settings: Settings) {
  const key = bedType ? BED_TEMPERATURE_KEYS.get(bedType) : undefined;
  return {
    firstLayer: temperatures(key ? settings?.[`${key}_initial_layer`] : undefined),
    otherLayers: temperatures(key ? settings?.[key] : undefined),
  };
}

export function formatBedTemperatures(values: Array<number | null>): string {
  return values.map(value => value === null ? 'Unknown' : `${value}°C`).join(' / ');
}
