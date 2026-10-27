import type { PrinterPrintMetadata } from '@/types/api';

const fields = [
  { key: 'printerModel', label: 'Printer model' },
  { key: 'profile', label: 'Profile' },
  { key: 'printTimeSeconds', label: 'Reported print time', unit: 's' },
  { key: 'estimatedPrintTimeSeconds', label: 'Estimated print time', unit: 's' },
  { key: 'estimatedPrintTimeNormalMode', label: 'Normal-mode estimate' },
  { key: 'layerHeightMm', label: 'Native layer height', unit: 'mm' },
  { key: 'maxLayerZMm', label: 'Maximum layer Z', unit: 'mm' },
  { key: 'fillDensity', label: 'Infill density' },
  { key: 'brimWidthMm', label: 'Brim width', unit: 'mm' },
  { key: 'supportMaterial', label: 'Support material' },
  { key: 'ironing', label: 'Ironing' },
  { key: 'bedTemperatureC', label: 'Bed temperature', unit: '\u00b0C' },
  { key: 'bedTemperaturePerToolC', label: 'Bed temperature per tool', unit: '\u00b0C' },
  { key: 'nozzleTemperatureC', label: 'Nozzle temperature', unit: '\u00b0C' },
  { key: 'nozzleTemperaturePerToolC', label: 'Nozzle temperature per tool', unit: '\u00b0C' },
  { key: 'nozzleDiameterMm', label: 'Nozzle diameter', unit: 'mm' },
  { key: 'nozzleDiameterPerToolMm', label: 'Nozzle diameter per tool', unit: 'mm' },
  { key: 'filamentType', label: 'Filament type' },
  { key: 'filamentTypePerTool', label: 'Filament type per tool' },
  { key: 'filamentUsedMm', label: 'Filament length', unit: 'mm' },
  { key: 'filamentUsedPerToolMm', label: 'Filament length per tool', unit: 'mm' },
  { key: 'filamentUsedG', label: 'Filament mass', unit: 'g' },
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

function formatValue(value: string | number | boolean | string[] | number[], unit?: string): string {
  if (Array.isArray(value)) {
    return value.map((item, index) => `Tool ${index + 1}: ${formatValue(item, unit)}`).join('; ');
  }
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  return unit ? `${value} ${unit}` : String(value);
}

/** Omit unknown fields, but keep explicit zero/false and every tool's original position. */
export function printerMetadataRows(metadata: PrinterPrintMetadata | null | undefined) {
  return fields.flatMap(({ key, label, unit }) => {
    const value = metadata?.[key];
    return value == null ? [] : [{ label, value: formatValue(value, unit) }];
  });
}
