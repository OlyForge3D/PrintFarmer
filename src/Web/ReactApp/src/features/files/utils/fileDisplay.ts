import { formatPrintTimeMinutes, parseApiDateTime } from '@/common/utils/datetime';
import { formatFileSize } from '@/common/utils/stlFileUtils';

/** Visual file category shared by the local library and printer-resident listings. */
export type FileKind = 'gcode' | 'model' | 'other';

const GCODE_EXTENSIONS = new Set(['gcode', 'bgcode', 'gco', 'g', 'ngc', 'gc']);
const MODEL_EXTENSIONS = new Set(['3mf', 'stl', 'step', 'stp', 'obj', 'ply']);

export function fileExtension(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.slice(dot + 1).toLowerCase() : '';
}

export function fileKindFromName(name: string): FileKind {
  const extension = fileExtension(name);
  if (GCODE_EXTENSIONS.has(extension)) return 'gcode';
  if (MODEL_EXTENSIONS.has(extension)) return 'model';
  return 'other';
}

export const FILE_KIND_LABEL: Record<FileKind, string> = {
  gcode: 'G-code file',
  model: '3D model',
  other: 'File',
};

/** Human-readable type: the kind plus the uppercase extension when there is one. */
export function fileTypeLabel(name: string, kind: FileKind = fileKindFromName(name)): string {
  const extension = fileExtension(name);
  return extension ? `${FILE_KIND_LABEL[kind]} (${extension.toUpperCase()})` : FILE_KIND_LABEL[kind];
}

/**
 * Parses an API timestamp into a valid Date, or null. API timestamps may omit the
 * trailing `Z`, which `parseApiDateTime` normalises to UTC.
 */
export function toValidDate(value?: string | Date | null): Date | null {
  if (!value) return null;
  if (value instanceof Date) return Number.isNaN(value.getTime()) ? null : value;
  const normalised = parseApiDateTime(value);
  if (!Number.isNaN(normalised.getTime())) return normalised;
  const direct = new Date(value);
  return Number.isNaN(direct.getTime()) ? null : direct;
}

const NUMERIC_PATTERN = /^-?\d+(\.\d+)?$/;
const ISO_DATE_PATTERN = /^\d{4}-\d{2}-\d{2}/;

/**
 * Interprets a provider's raw modification value. The server only retains values it
 * already parsed as Unix epoch seconds (Moonraker `modified`, OctoPrint `date`, PrusaLink
 * `m_timestamp`), so a bare number is epoch seconds. ISO strings are accepted too. Any
 * other value returns null and callers show the raw string rather than guessing.
 */
export function parseProviderModified(raw?: string | null): Date | null {
  const value = raw?.trim();
  if (!value) return null;
  if (NUMERIC_PATTERN.test(value)) {
    const date = new Date(Number(value) * 1000);
    return Number.isNaN(date.getTime()) ? null : date;
  }
  return ISO_DATE_PATTERN.test(value) ? toValidDate(value) : null;
}

export function formatSizeLabel(bytes: number | null | undefined, unknown = 'Unknown'): string {
  return bytes == null || !Number.isFinite(bytes) || bytes < 0 ? unknown : formatFileSize(bytes);
}

export const UNAVAILABLE = 'Unavailable';

/** Formats a duration expressed in minutes (local G-code metadata). */
export function formatPrintMinutes(minutes: number | null | undefined): string {
  return minutes == null || !Number.isFinite(minutes) || minutes <= 0
    ? UNAVAILABLE : formatPrintTimeMinutes(minutes);
}

/** Formats a duration expressed in seconds (printer preview metadata). */
export function formatPrintSeconds(seconds: number | null | undefined): string {
  return seconds == null || !Number.isFinite(seconds) || seconds <= 0
    ? UNAVAILABLE : formatPrintTimeMinutes(seconds / 60);
}

export function formatLayerHeight(millimetres: number | null | undefined): string {
  return millimetres == null || !Number.isFinite(millimetres) || millimetres <= 0
    ? UNAVAILABLE : `${millimetres} mm`;
}

/** Server reason codes are snake_case identifiers; show them as plain words. */
export function describeReasonCode(code: string | null | undefined): string | null {
  return code ? code.replaceAll('_', ' ') : null;
}