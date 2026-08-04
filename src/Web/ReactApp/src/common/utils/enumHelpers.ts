import { PrinterBackend, MotionType, PrinterBackendString } from '@/types/api';

/**
 * Utility type for enum option used in select dropdowns.
 *
 * `value` is a string because the wire contract for these enums is a string
 * (see PrinterBackend/MotionType in types/api.ts).
 */
export interface EnumOption {
  value: string;
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
 * Legacy numeric encodings that may still exist in persisted client state
 * (localStorage, cached query data) written before these enums were corrected
 * to match the string wire contract.
 */
const LEGACY_PRINTER_BACKEND_BY_ORDINAL: Record<number, PrinterBackend> = {
  0: PrinterBackend.Unknown,
  1: PrinterBackend.Moonraker,
  2: PrinterBackend.PrusaLink,
  3: PrinterBackend.SDCP,
  4: PrinterBackend.OctoPrint,
  5: PrinterBackend.FlashForge,
};

const LEGACY_MOTION_TYPE_BY_ORDINAL: Record<number, MotionType> = {
  0: MotionType.Cartesian,
  1: MotionType.CoreXY,
  2: MotionType.Delta,
  99: MotionType.Unknown,
};

/**
 * Coerce an unknown backend value into a PrinterBackend.
 *
 * Accepts the current string wire values (case-insensitively) and tolerates
 * legacy numeric ordinals from persisted client state. Returns undefined when
 * the value cannot be recognised, so callers can decide on a fallback.
 */
export function toPrinterBackend(value: unknown): PrinterBackend | undefined {
  if (value === null || value === undefined) return undefined;

  if (typeof value === 'number') {
    return LEGACY_PRINTER_BACKEND_BY_ORDINAL[value];
  }

  if (typeof value === 'string') {
    const trimmed = value.trim();
    if (trimmed !== '' && Number.isInteger(Number(trimmed))) {
      return LEGACY_PRINTER_BACKEND_BY_ORDINAL[Number(trimmed)];
    }
    return Object.values(PrinterBackend).find(
      (backend) => backend.toLowerCase() === trimmed.toLowerCase()
    );
  }

  return undefined;
}

/**
 * Coerce an unknown motion type value into a MotionType.
 * Mirrors {@link toPrinterBackend}.
 */
export function toMotionType(value: unknown): MotionType | undefined {
  if (value === null || value === undefined) return undefined;

  if (typeof value === 'number') {
    return LEGACY_MOTION_TYPE_BY_ORDINAL[value];
  }

  if (typeof value === 'string') {
    const trimmed = value.trim();
    if (trimmed !== '' && Number.isInteger(Number(trimmed))) {
      return LEGACY_MOTION_TYPE_BY_ORDINAL[Number(trimmed)];
    }
    return Object.values(MotionType).find(
      (motionType) => motionType.toLowerCase() === trimmed.toLowerCase()
    );
  }

  return undefined;
}

/**
 * Get all PrinterBackend enum values as select options.
 */
export function getPrinterBackendOptions(): EnumOption[] {
  const orderedBackends = [
    PrinterBackend.Moonraker,
    PrinterBackend.PrusaLink,
    PrinterBackend.SDCP,
    PrinterBackend.OctoPrint,
    PrinterBackend.FlashForge,
  ];

  return orderedBackends.map((backend) => ({
    value: backend,
    label: backend,
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
 * Convert a PrinterBackendString to PrinterBackend.
 *
 * The enum now matches the wire contract, so this is effectively a validating
 * pass-through. Retained because call sites still express intent through it.
 */
export function printerBackendStringToEnum(
  value: PrinterBackendString | undefined
): PrinterBackend | undefined {
  if (!value) return undefined;
  return toPrinterBackend(value);
}

/**
 * Convert a PrinterBackend to PrinterBackendString.
 * `Unknown` has no PrinterBackendString representation and maps to undefined.
 */
export function printerBackendEnumToString(
  value: PrinterBackend | undefined
): PrinterBackendString | undefined {
  if (value === undefined || value === PrinterBackend.Unknown) return undefined;
  return value as PrinterBackendString;
}

/**
 * Get the display name for a PrinterBackend value.
 * Tolerates legacy numeric values from persisted state.
 */
export function getPrinterBackendName(
  backend: PrinterBackend | string | number | undefined
): string {
  return toPrinterBackend(backend) ?? '';
}

/**
 * Get all MotionType enum values as select options
 * This automatically includes all motion types defined in the enum
 */
export function getMotionTypeOptions(): EnumOption[] {
  return Object.values(MotionType).map((motionType) => ({
    value: motionType,
    label: motionType,
  }));
}

/**
 * Get the display name for a MotionType value.
 * Tolerates legacy numeric values from persisted state.
 */
export function getMotionTypeName(
  motionType: MotionType | string | number | undefined
): string {
  return toMotionType(motionType) ?? '';
}
