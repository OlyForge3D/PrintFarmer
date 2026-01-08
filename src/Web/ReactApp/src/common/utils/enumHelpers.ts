import { PrinterBackend, MotionType } from '@/types/api';

/**
 * Utility type for enum option used in select dropdowns
 */
export interface EnumOption {
  value: number;
  label: string;
}

/**
 * Get all PrinterBackend enum values as select options
 * This automatically includes all backends defined in the enum
 */
export function getPrinterBackendOptions(): EnumOption[] {
  const orderedBackends = [
    PrinterBackend.Moonraker,
    PrinterBackend.PrusaLink,
    PrinterBackend.SDCP,
    PrinterBackend.OctoPrint,
  ];

  return orderedBackends
    .filter((backend) => backend !== undefined)
    .map((backend) => ({
      value: backend,
      label: PrinterBackend[backend],
    }));
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
