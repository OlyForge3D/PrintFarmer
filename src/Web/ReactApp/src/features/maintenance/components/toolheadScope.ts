/**
 * Sentinel + helpers for the per-toolhead maintenance scope picker.
 * Extracted from ToolheadScopePicker.tsx so the picker file only exports its
 * component (react-refresh/only-export-components).
 */

/**
 * Sentinel value representing the "printer-wide" maintenance scope.
 * Callers translate this to `toolheadId: null` when talking to the API.
 */
export const PRINTER_WIDE_SCOPE = '__printer_wide__' as const;

export type ToolheadScopeValue = typeof PRINTER_WIDE_SCOPE | string;

/**
 * Convert the picker value to the API-facing `toolheadId` (nullable string).
 */
export function toolheadIdFromScope(value: ToolheadScopeValue): string | null {
  return value === PRINTER_WIDE_SCOPE ? null : value;
}

/**
 * Convert an API `toolheadId` (nullable string) to the picker's value.
 */
export function scopeFromToolheadId(toolheadId: string | null | undefined): ToolheadScopeValue {
  return toolheadId == null ? PRINTER_WIDE_SCOPE : toolheadId;
}
