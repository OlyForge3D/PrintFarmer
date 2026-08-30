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
 * Validates the print-quality fields of a raw OrcaProcessSettings object.
 * Both Simple and Advanced slicer modes write into the same underlying
 * OrcaProcessSettings state, so this single check covers either path plus
 * profile import. Fields left `undefined` are unset (a valid default is
 * applied elsewhere) and are skipped rather than flagged.
 */
export function validateOrcaPrintSettings(settings: OrcaProcessSettings): SlicerFieldError[] {
  const errors: SlicerFieldError[] = [];

  if (typeof settings.wall_loops === 'number') {
    const message = validateWallLoops(settings.wall_loops);
    if (message) errors.push({ field: 'wallLoops', message });
  }
  if (typeof settings.sparse_infill_density === 'number') {
    const message = validateInfillPercent(settings.sparse_infill_density);
    if (message) errors.push({ field: 'infillPercent', message });
  }
  if (typeof settings.top_shell_layers === 'number') {
    const message = validateTopShellLayers(settings.top_shell_layers);
    if (message) errors.push({ field: 'topShellLayers', message });
  }
  if (typeof settings.bottom_shell_layers === 'number') {
    const message = validateBottomShellLayers(settings.bottom_shell_layers);
    if (message) errors.push({ field: 'bottomShellLayers', message });
  }

  return errors;
}
