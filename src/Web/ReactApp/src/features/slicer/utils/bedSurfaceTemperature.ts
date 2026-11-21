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

const NATIVE_BED_TYPES = [
  'Cool Plate', 'Engineering Plate', 'High Temp Plate',
  'Textured PEI Plate', 'Textured Cool Plate', 'Supertack Plate',
];

export interface FilamentBedContext {
  bedType?: string;
  supportMultiBedTypes?: boolean;
}

/** Matches Preset::get_bed_type for a resolved printer preset and model. */
export function getNativeDefaultBedType(machine: Settings, modelId: string | undefined): string | undefined {
  if (!machine) return undefined;
  const raw = machine.default_bed_type;
  if (raw !== undefined && raw !== null && typeof raw !== 'string') return undefined;
  if (typeof raw === 'string' && raw !== '') {
    const number = Number.parseInt(raw, 10);
    if (!Number.isFinite(number) || number <= 0) return 'High Temp Plate';
    return NATIVE_BED_TYPES[number - 1];
  }
  if (!modelId) return undefined;
  return ['BL-P001', 'BL-P002', 'C13'].includes(modelId) ? 'Cool Plate' : 'High Temp Plate';
}

export function getNativeBedContext(
  machine: Settings,
  modelId: string | undefined,
  manufacturer: string | undefined,
  selectedBedType: string,
): FilamentBedContext {
  if (!machine) return {};
  const vendor = manufacturer?.trim().toLowerCase();
  const flag = machine.support_multi_bed_types;
  const multi = ['bbl', 'bambu', 'bambu lab', 'bambulab'].includes(vendor ?? '')
    ? true : flag === undefined ? false : readNativeBoolean(flag);
  const defaultBed = getNativeDefaultBedType(machine, modelId);
  return {
    supportMultiBedTypes: multi,
    bedType: multi === true && selectedBedType ? readBedType(selectedBedType) : defaultBed,
  };
}

function readNativeBoolean(value: unknown): boolean | undefined {
  if (Array.isArray(value) && value.length === 1) [value] = value;
  if (typeof value === 'string') value = value.trim().toLowerCase();
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
  const supportMultiBedTypes = isBambu || readNativeBoolean(machine?.support_multi_bed_types);
  return {
    bedType: resolveBedType(supportMultiBedTypes === false ? '' : selectedBedType, process, machine),
    supportMultiBedTypes,
  };
}

/**
 * Orca defaults to Cool Plate and a single bed only after all profile layers are
 * resolved. A 3MF can supply a per-plate bed type that overrides those layers.
 */
export function getFilamentEditorBedContext(
  machine: Settings,
  process: Settings,
  selectedBedType: string,
  manufacturer: string | undefined,
  options: { machineResolved: boolean; processResolved: boolean; projectMayOverride: boolean },
): FilamentBedContext {
  const context = getFilamentBedContext(machine, process, selectedBedType, manufacturer);
  if (!options.machineResolved) return { ...context, supportMultiBedTypes: context.supportMultiBedTypes === true ? true : undefined };
  const flag = machine?.support_multi_bed_types;
  const supportMultiBedTypes = context.supportMultiBedTypes ?? (flag === undefined ? false : undefined);
  if (options.projectMayOverride || !options.processResolved) return { supportMultiBedTypes };
  const processBed = process?.curr_bed_type;
  const machineBed = machine?.curr_bed_type;
  const bedType = selectedBedType
    || (processBed !== undefined ? readBedType(processBed)
      : machineBed !== undefined ? readBedType(machineBed) : 'Cool Plate');
  return { supportMultiBedTypes, bedType };
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
