/** Extensions recognized as G-code files. */
export const GCODE_EXTENSIONS = ['.gcode', '.bgcode', '.g', '.gco'] as const;
export const TEXT_GCODE_EXTENSIONS = ['.gcode', '.g', '.gco'] as const;

export function isGcodeFileName(fileName: string): boolean {
  const lower = fileName.toLowerCase();
  return GCODE_EXTENSIONS.some(ext => lower.endsWith(ext));
}

export function isTextGcodeFileName(fileName: string): boolean {
  const lower = fileName.toLowerCase();
  return TEXT_GCODE_EXTENSIONS.some(ext => lower.endsWith(ext));
}
