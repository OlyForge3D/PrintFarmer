import type { PrinterFilamentTool, PrinterPrintMetadata } from '@/types/api';

const DEGREES_C = '\u00b0C';

/**
 * Supplied-only native fields listed after the grouped print settings. Fields the grouped
 * settings already read (slicer, sliced-for model, temperatures, nozzle, infill, supports,
 * filament totals, tools) are deliberately absent so no value is shown twice.
 */
const fields = [
  { key: 'profile', label: 'Profile' },
  { key: 'printTimeSeconds', label: 'Reported print time', unit: 's' },
  { key: 'estimatedPrintTimeSeconds', label: 'Estimated print time', unit: 's' },
  { key: 'estimatedPrintTimeNormalMode', label: 'Normal-mode estimate' },
  { key: 'layerHeightMm', label: 'Native layer height', unit: 'mm' },
  { key: 'maxLayerZMm', label: 'Maximum layer Z', unit: 'mm' },
  { key: 'brimWidthMm', label: 'Brim width', unit: 'mm' },
  { key: 'supportMaterial', label: 'Support material' },
  { key: 'ironing', label: 'Ironing' },
  { key: 'bedTemperaturePerToolC', label: 'Bed temperature per tool', unit: DEGREES_C },
  { key: 'nozzleTemperaturePerToolC', label: 'Nozzle temperature per tool', unit: DEGREES_C },
  { key: 'nozzleDiameterPerToolMm', label: 'Nozzle diameter per tool', unit: 'mm' },
  { key: 'filamentTypePerTool', label: 'Filament type per tool' },
  { key: 'filamentUsedPerToolMm', label: 'Filament length per tool', unit: 'mm' },
  { key: 'filamentUsedPerToolG', label: 'Filament mass per tool', unit: 'g' },
  { key: 'filamentUsedCm3', label: 'Filament volume', unit: 'cm\u00b3' },
  { key: 'filamentUsedPerToolCm3', label: 'Filament volume per tool', unit: 'cm\u00b3' },
  { key: 'filamentCost', label: 'Filament cost', unit: '(currency unspecified)' },
  { key: 'filamentCostPerTool', label: 'Filament cost per tool', unit: '(currency unspecified)' },
  { key: 'materialName', label: 'Material name' },
  { key: 'requiredResinMl', label: 'Required resin', unit: 'mL' },
  { key: 'fadedLayers', label: 'Faded layers' },
  { key: 'exposureTimeSeconds', label: 'Exposure time', unit: 's' },
  { key: 'initialExposureTimeSeconds', label: 'Initial exposure time', unit: 's' },
  { key: 'minExposureTimeSeconds', label: 'Minimum exposure time', unit: 's' },
  { key: 'maxExposureTimeSeconds', label: 'Maximum exposure time', unit: 's' },
  { key: 'minInitialExposureTimeSeconds', label: 'Minimum initial exposure time', unit: 's' },
  { key: 'maxInitialExposureTimeSeconds', label: 'Maximum initial exposure time', unit: 's' },
  { key: 'normalPercentPresent', label: 'Normal-mode progress reporting' },
  { key: 'normalLeftPresent', label: 'Normal-mode remaining-time reporting' },
  { key: 'quietPercentPresent', label: 'Quiet-mode progress reporting' },
  { key: 'quietLeftPresent', label: 'Quiet-mode remaining-time reporting' },
  { key: 'layerInfoPresent', label: 'Layer information present' },
] satisfies Array<{ key: keyof PrinterPrintMetadata; label: string; unit?: string }>;

/** Significant digits kept when printing numbers; trims binary noise (0.1 + 0.2) only. */
const DISPLAY_PRECISION = 12;

/** Literal RGB/RGBA hex only; anything else is unknown, never a CSS expression or guessed color. */
const HEX_COLOR = /^#(?:[0-9a-f]{6}|[0-9a-f]{8})$/i;

function formatNumber(value: number): string {
  return String(Number.parseFloat(value.toPrecision(DISPLAY_PRECISION)));
}

/** A finite number with its unit, or null (unknown). Zero is data, not "unknown". */
function measurement(value: number | null | undefined, unit?: string): string | null {
  if (value == null || !Number.isFinite(value)) return null;
  return unit ? `${formatNumber(value)} ${unit}` : formatNumber(value);
}

function text(value: string | null | undefined): string | null {
  return typeof value === 'string' && value.trim() ? value : null;
}

function formatValue(value: string | number | boolean | string[] | number[], unit?: string): string {
  if (Array.isArray(value)) {
    return value.map((item, index) => `Tool ${index + 1}: ${formatValue(item, unit)}`).join('; ');
  }
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  if (typeof value === 'number') return measurement(value, unit) ?? String(value);
  return unit ? `${value} ${unit}` : value;
}

/** Omit unknown fields, but keep explicit zero/false and every tool's original position. */
export function printerMetadataRows(metadata: PrinterPrintMetadata | null | undefined) {
  return fields.flatMap(({ key, label, unit }) => {
    const value = metadata?.[key];
    return value == null ? [] : [{ label, value: formatValue(value, unit) }];
  });
}

/** One grouped print setting; `value: null` means the source did not report it (shown as "—"). */
export interface PrintSettingRow {
  label: string;
  value: string | null;
}

function supportsText(enabled: boolean | null | undefined): string | null {
  if (enabled == null) return null;
  return enabled ? 'Enabled' : 'Disabled';
}

function filamentUsedText(metadata: PrinterPrintMetadata): string | null {
  const grams = measurement(metadata.filamentUsedG, 'g');
  if (grams == null) return null;
  return metadata.filamentUsedGIsDerived === true ? `${grams} (sum of tools)` : grams;
}

/**
 * The grouped settings every printer file shows, in a fixed order so files are comparable.
 * Unknown stays null - never zero, "disabled" or a guessed value. Support type/style are listed
 * only when supplied; "Supports" alone carries the unknown-vs-disabled distinction.
 */
export function printSettingRows(metadata: PrinterPrintMetadata | null | undefined): PrintSettingRow[] {
  const m: PrinterPrintMetadata = metadata ?? {};
  const slicer = [text(m.slicerName), text(m.slicerVersion)].filter(Boolean).join(' ');
  const infill = m.infillPercentage != null && Number.isFinite(m.infillPercentage)
    ? `${formatNumber(m.infillPercentage)}%`
    : text(m.fillDensity);
  const supportType = text(m.supportType);
  const supportStyle = text(m.supportStyle);
  return [
    { label: 'Slicer', value: slicer || null },
    { label: 'Sliced for', value: text(m.printerModel) },
    { label: 'Layer count', value: measurement(m.layerCount) },
    { label: 'Nozzle diameter', value: measurement(m.nozzleDiameterMm, 'mm') },
    { label: 'Nozzle temperature', value: measurement(m.nozzleTemperatureC, DEGREES_C) },
    { label: 'Bed temperature', value: measurement(m.bedTemperatureC, DEGREES_C) },
    { label: 'Infill', value: infill },
    { label: 'Infill pattern', value: text(m.infillPattern) },
    { label: 'Supports', value: supportsText(m.supportEnabled) },
    ...(supportType ? [{ label: 'Support type', value: supportType }] : []),
    ...(supportStyle ? [{ label: 'Support style', value: supportStyle }] : []),
    { label: 'Filament type', value: text(m.filamentType) },
    { label: 'Filament used', value: filamentUsedText(m) },
    { label: 'Filament length', value: measurement(m.filamentUsedMm, 'mm') },
  ];
}

/** One source tool slot, ready to render. Null fields are unknown. */
export interface FilamentToolRow {
  /** Stable React key derived from the source tool index (unique per metadata record). */
  key: string;
  label: string;
  /** Validated literal hex color, uppercased; null when missing or not a literal color. */
  color: string | null;
  material: string | null;
  used: string | null;
}

function isTool(tool: PrinterFilamentTool | null | undefined): tool is PrinterFilamentTool {
  return tool != null && Number.isSafeInteger(tool.index) && tool.index >= 0;
}

/**
 * Tools ordered by their explicit source index (never by list position), labelled 1-based to
 * match the "Tool n" wording of the per-tool native fields, with the native id when supplied.
 */
export function filamentToolRows(metadata: PrinterPrintMetadata | null | undefined): FilamentToolRow[] {
  const supplied = metadata?.tools;
  const tools = Array.isArray(supplied) ? supplied : [];
  return [...tools].filter(isTool).sort((a, b) => a.index - b.index).map((tool) => {
    const sourceId = text(tool.sourceId);
    const number = `Tool ${tool.index + 1}`;
    return {
      key: `tool-${tool.index}`,
      label: sourceId ? `${number} (${sourceId})` : number,
      color: typeof tool.color === 'string' && HEX_COLOR.test(tool.color) ? tool.color.toUpperCase() : null,
      material: text(tool.material),
      used: measurement(tool.filamentUsedG, 'g'),
    };
  });
}
