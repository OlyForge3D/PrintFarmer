import { PrinterBackend, MotionType, PrinterBackendString } from '@/types/api';

/**
 * Utility type for enum option used in select dropdowns
 */
export interface EnumOption {
  value: number;
  label: string;
}

/**
 * Utility type for string-based enum options (for API compatibility)
 */
export interface StringEnumOption {
  value: string;
  label: string;
}

/**
 * Get all PrinterBackend enum values as select options (numeric values)
 * This automatically includes all backends defined in the enum
 */
export function getPrinterBackendOptions(): EnumOption[] {
  const orderedBackends = [
    PrinterBackend.Moonraker,
    PrinterBackend.PrusaLink,
    PrinterBackend.SDCP,
    PrinterBackend.OctoPrint,
    PrinterBackend.FlashForge,
  ];

  return orderedBackends
    .filter((backend) => backend !== undefined)
    .map((backend) => ({
      value: backend,
      label: PrinterBackend[backend],
    }));
}

/**
 * Get all PrinterBackend enum values as string options (for API compatibility)
 * Returns string values like "Moonraker", "PrusaLink", etc.
 */
export function getPrinterBackendStringOptions(): StringEnumOption[] {
  const orderedBackends: PrinterBackendString[] = [
    'Moonraker',
    'PrusaLink',
    'SDCP',
    'OctoPrint',
    'FlashForge',
  ];

  return orderedBackends.map((backend) => ({
    value: backend,
    label: backend,
  }));
}

/**
 * Convert a PrinterBackendString to PrinterBackend (numeric enum)
 */
export function printerBackendStringToEnum(value: PrinterBackendString | undefined): PrinterBackend | undefined {
  if (!value) return undefined;
  const mapping: Record<PrinterBackendString, PrinterBackend> = {
    'Moonraker': PrinterBackend.Moonraker,
    'PrusaLink': PrinterBackend.PrusaLink,
    'SDCP': PrinterBackend.SDCP,
    'OctoPrint': PrinterBackend.OctoPrint,
    'FlashForge': PrinterBackend.FlashForge,
  };
  return mapping[value];
}

/**
 * Convert a PrinterBackend (numeric enum) to PrinterBackendString
 */
export function printerBackendEnumToString(value: PrinterBackend | undefined): PrinterBackendString | undefined {
  if (value === undefined) return undefined;
  const mapping: Record<PrinterBackend, PrinterBackendString | undefined> = {
    [PrinterBackend.Unknown]: undefined,
    [PrinterBackend.Moonraker]: 'Moonraker',
    [PrinterBackend.PrusaLink]: 'PrusaLink',
    [PrinterBackend.SDCP]: 'SDCP',
    [PrinterBackend.OctoPrint]: 'OctoPrint',
    [PrinterBackend.FlashForge]: 'FlashForge',
  };
  return mapping[value];
}

/**
 * Get the display name for a PrinterBackend enum value
 */
export function getPrinterBackendName(backend: PrinterBackend | undefined): string {
  if (backend === undefined) return '';
  return PrinterBackend[backend] || '';
}

/**
 * Get all MotionType enum values as select options
 * This automatically includes all motion types defined in the enum
 */
export function getMotionTypeOptions(): EnumOption[] {
  return Object.entries(MotionType)
    .filter(([, value]) => typeof value === 'number')
    .map(([key, value]) => ({
      value: value as number,
      label: key,
    }));
}

/**
 * Get the display name for a MotionType enum value
 */
export function getMotionTypeName(motionType: MotionType | undefined): string {
  if (motionType === undefined) return '';
  return MotionType[motionType] || '';
}
