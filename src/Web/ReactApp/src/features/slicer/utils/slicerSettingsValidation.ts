/**
 * Shared print-quality field validation for the slicer settings forms.
 *
 * Issue #2223: the slicer accepted negative perimeter/infill values and a
 * zero top/bottom shell layer count, created a slice job anyway, and only
 * reported a generic "Slicing failed" once the backend/worker rejected the
 * unsliceable settings. These pure validators are used both by the Simple
 * mode settings panel (inline, per-field feedback as the user types) and by
 * the slice-job submit guard in NewSliceJobPage (a defense-in-depth check
 * against the raw OrcaProcessSettings object, which is also reachable via
 * Advanced mode and profile import — paths the Simple panel's inline
 * validation cannot see).
 */
import type { OrcaProcessSettings } from '@/features/slicer/components/settings/slicerSettingsTypes';

export type SlicerValidatedField = 'wallLoops' | 'infillPercent' | 'topShellLayers' | 'bottomShellLayers';

export interface SlicerFieldError {
  field: SlicerValidatedField;
  message: string;
}

/** Perimeters (wall loops): at least one wall is required to print anything. */
export function validateWallLoops(value: number): string | null {
  if (!Number.isFinite(value) || value < 1) {
    return 'Perimeters must be at least 1.';
  }
  return null;
}

/** Infill density: a negative percentage is meaningless. */
export function validateInfillPercent(value: number): string | null {
  if (!Number.isFinite(value) || value < 0) {
    return 'Infill density cannot be negative.';
  }
  return null;
}

/** Top shell layers: zero solid top layers leaves the print's interior exposed. */
export function validateTopShellLayers(value: number): string | null {
  if (!Number.isFinite(value) || value < 1) {
    return 'Top layers must be at least 1.';
  }
  return null;
}

/** Bottom shell layers: zero solid bottom layers leaves the first layers unsupported. */
export function validateBottomShellLayers(value: number): string | null {
  if (!Number.isFinite(value) || value < 1) {
    return 'Bottom layers must be at least 1.';
  }
  return null;
}

/**
 * Rejects only genuinely nonsensical values (negative or non-finite) without
 * imposing the stricter ">= 1" floor. OrcaSlicer's own settings metadata
 * declares `min: 0` for wall_loops/top_shell_layers/bottom_shell_layers, and
 * Advanced-mode features such as Spiral vase (`spiral_mode`) *require*
 * `top_shell_layers: 0` to slice at all — so a defense-in-depth check that
 * covers Advanced mode and profile import must not flag a legitimate zero.
 */
function validateNonNegative(value: number, label: string): string | null {
  if (!Number.isFinite(value) || value < 0) {
    return `${label} cannot be negative.`;
  }
  return null;
}

/**
 * Coerces a settings field to a finite number, tolerating the string/array
 * encodings OrcaSlicer configs and profile imports sometimes use, so this
 * check isn't silently bypassed just because a value wasn't stored as a raw
 * JS `number`. Returns `undefined` if the field is genuinely absent/unset.
 */
function coerceToNumber(value: unknown): number | undefined {
  if (value === undefined || value === null || value === '') {
    return undefined;
  }
  if (typeof value === 'number') {
    return value;
  }
  if (typeof value === 'string') {
    // Use parseFloat (matching this repo's toNumber convention in
    // metadataTypes.ts) rather than Number(): OrcaSlicer percent-typed
    // fields like sparse_infill_density are sometimes encoded as "15%",
    // which Number() rejects as NaN but parseFloat correctly reads as 15.
    const parsed = parseFloat(value);
    return Number.isFinite(parsed) ? parsed : NaN;
  }
  if (Array.isArray(value) && value.length > 0) {
    return coerceToNumber(value[0]);
  }
  return undefined;
}

/**
 * Validates the print-quality fields of a raw OrcaProcessSettings object.
 * Both Simple and Advanced slicer modes write into the same underlying
 * OrcaProcessSettings state, so this single check covers either path plus
 * profile import. Fields left unset are skipped rather than flagged.
 *
 * This is intentionally more lenient than the Simple-mode inline validators
 * above (which require ">= 1" for wall loops/shell layers): it is a
 * defense-in-depth net against paths the Simple panel's live per-field
 * validation cannot see (Advanced mode, profile import), and Advanced mode
 * legitimately allows zero for these fields (see `validateNonNegative`).
 * Only a negative or non-finite value indicates the exact bug in #2223.
 */
export function validateOrcaPrintSettings(settings: OrcaProcessSettings): SlicerFieldError[] {
  const errors: SlicerFieldError[] = [];

  const wallLoops = coerceToNumber(settings.wall_loops);
  if (wallLoops !== undefined) {
    const message = validateNonNegative(wallLoops, 'Wall loops');
    if (message) errors.push({ field: 'wallLoops', message });
  }
  const infillPercent = coerceToNumber(settings.sparse_infill_density);
  if (infillPercent !== undefined) {
    const message = validateNonNegative(infillPercent, 'Infill density');
    if (message) errors.push({ field: 'infillPercent', message });
  }
  const topShellLayers = coerceToNumber(settings.top_shell_layers);
  if (topShellLayers !== undefined) {
    const message = validateNonNegative(topShellLayers, 'Top shell layers');
    if (message) errors.push({ field: 'topShellLayers', message });
  }
  const bottomShellLayers = coerceToNumber(settings.bottom_shell_layers);
  if (bottomShellLayers !== undefined) {
    const message = validateNonNegative(bottomShellLayers, 'Bottom shell layers');
    if (message) errors.push({ field: 'bottomShellLayers', message });
  }

  return errors;
}
