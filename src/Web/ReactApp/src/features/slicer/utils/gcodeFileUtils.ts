/** Extensions recognized as G-code files. */
export const GCODE_EXTENSIONS = ['.gcode', '.bgcode', '.g'] as const;

export function isGcodeFileName(fileName: string): boolean {
  const lower = fileName.toLowerCase();
  return GCODE_EXTENSIONS.some(ext => lower.endsWith(ext));
}
