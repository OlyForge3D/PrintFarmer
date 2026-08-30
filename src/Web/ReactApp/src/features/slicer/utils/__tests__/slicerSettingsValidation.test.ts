import { describe, expect, it } from 'vitest';
import {
  validateBottomShellLayers,
  validateInfillPercent,
  validateOrcaPrintSettings,
  validateTopShellLayers,
  validateWallLoops
} from '../slicerSettingsValidation';
import type { OrcaProcessSettings } from '@/features/slicer/components/settings/slicerSettingsTypes';

// Issue #2223: the slicer accepted negative perimeter/infill values and a
// zero top/bottom shell layer count, created a slice job anyway, and only
// reported a generic "Slicing failed" once the unsliceable settings reached
// the worker. These tests cover the shared validators directly so the exact
// rejection rules are pinned down independently of any component's wiring.

describe('Simple-mode inline field validators (strict, ">= 1")', () => {
  it.each([-5, -1, 0])('validateWallLoops rejects %d', (value) => {
    expect(validateWallLoops(value)).toBe('Perimeters must be at least 1.');
  });

  it('validateWallLoops accepts a positive value', () => {
    expect(validateWallLoops(3)).toBeNull();
  });

  it.each([-10, -1, 0])('validateTopShellLayers rejects %d', (value) => {
    expect(validateTopShellLayers(value)).toBe('Top layers must be at least 1.');
  });

  it('validateTopShellLayers accepts a positive value', () => {
    expect(validateTopShellLayers(4)).toBeNull();
  });

  it.each([-10, -1, 0])('validateBottomShellLayers rejects %d', (value) => {
    expect(validateBottomShellLayers(value)).toBe('Bottom layers must be at least 1.');
  });

  it('validateBottomShellLayers accepts a positive value', () => {
    expect(validateBottomShellLayers(4)).toBeNull();
  });

  it.each([-5, -1])('validateInfillPercent rejects %d', (value) => {
    expect(validateInfillPercent(value)).toBe('Infill density cannot be negative.');
  });

  it('validateInfillPercent accepts zero (no infill is a valid choice)', () => {
    expect(validateInfillPercent(0)).toBeNull();
  });
});

describe('validateOrcaPrintSettings — lenient aggregate/defense-in-depth guard', () => {
  const baseSettings: OrcaProcessSettings = {};

  it('rejects a negative wall_loops', () => {
    const errors = validateOrcaPrintSettings({ ...baseSettings, wall_loops: -1 });
    expect(errors).toEqual([{ field: 'wallLoops', message: 'Wall loops cannot be negative.' }]);
  });

  it('rejects a negative sparse_infill_density', () => {
    const errors = validateOrcaPrintSettings({ ...baseSettings, sparse_infill_density: -10 });
    expect(errors).toEqual([{ field: 'infillPercent', message: 'Infill density cannot be negative.' }]);
  });

  it('rejects a negative top_shell_layers', () => {
    const errors = validateOrcaPrintSettings({ ...baseSettings, top_shell_layers: -3 });
    expect(errors).toEqual([{ field: 'topShellLayers', message: 'Top shell layers cannot be negative.' }]);
  });

  it('rejects a negative bottom_shell_layers', () => {
    const errors = validateOrcaPrintSettings({ ...baseSettings, bottom_shell_layers: -2 });
    expect(errors).toEqual([{ field: 'bottomShellLayers', message: 'Bottom shell layers cannot be negative.' }]);
  });

  // Regression guard for a review finding (#2223): OrcaSlicer's own settings
  // metadata declares `min: 0` for wall_loops/top_shell_layers/bottom_shell_layers,
  // and Advanced-mode Spiral vase (`spiral_mode`) *requires* `top_shell_layers: 0`
  // to slice at all. The aggregate guard must not reject a legitimate zero —
  // only Simple mode's stricter inline validators (above) do that.
  it.each([
    ['wall_loops', 0],
    ['top_shell_layers', 0],
    ['bottom_shell_layers', 0],
    ['sparse_infill_density', 0]
  ] as const)('allows a zero %s (legitimate Advanced-mode / spiral-vase configuration)', (field, value) => {
    const errors = validateOrcaPrintSettings({ ...baseSettings, [field]: value });
    expect(errors).toEqual([]);
  });

  it('returns no errors for an empty settings object (unset fields are skipped)', () => {
    expect(validateOrcaPrintSettings({})).toEqual([]);
  });

  it('returns no errors for entirely valid settings', () => {
    const errors = validateOrcaPrintSettings({
      wall_loops: 3,
      sparse_infill_density: 15,
      top_shell_layers: 4,
      bottom_shell_layers: 4
    });
    expect(errors).toEqual([]);
  });

  it('reports every invalid field at once, not just the first', () => {
    const errors = validateOrcaPrintSettings({
      wall_loops: -1,
      sparse_infill_density: -10,
      top_shell_layers: -3,
      bottom_shell_layers: -2
    });
    expect(errors).toHaveLength(4);
    expect(errors.map((e) => e.field).sort()).toEqual(
      ['bottomShellLayers', 'infillPercent', 'topShellLayers', 'wallLoops'].sort()
    );
  });

  // Defensive coercion (review finding #2223): profile imports / OrcaSlicer
  // configs sometimes encode numeric fields as strings or single-element
  // string arrays rather than raw JS numbers. The guard must not silently
  // bypass validation just because a value wasn't already a `number`.
  it('coerces a string-encoded negative value and rejects it', () => {
    const settings = { wall_loops: '-1' } as unknown as OrcaProcessSettings;
    const errors = validateOrcaPrintSettings(settings);
    expect(errors).toEqual([{ field: 'wallLoops', message: 'Wall loops cannot be negative.' }]);
  });

  it('coerces a single-element array-encoded negative value and rejects it', () => {
    const settings = { top_shell_layers: ['-2'] } as unknown as OrcaProcessSettings;
    const errors = validateOrcaPrintSettings(settings);
    expect(errors).toEqual([{ field: 'topShellLayers', message: 'Top shell layers cannot be negative.' }]);
  });

  it('treats an empty string as unset rather than an invalid number', () => {
    const settings = { bottom_shell_layers: '' } as unknown as OrcaProcessSettings;
    expect(validateOrcaPrintSettings(settings)).toEqual([]);
  });

  // Bishop review (issue #2223): sparse_infill_density is a coPercent field
  // and this repo's OrcaSlicer configs encode percent values as "15%"
  // strings (see processSettingsBaseline.test.ts, slicerProfilePayload.test.ts).
  // Number("15%") is NaN, which would wrongly report "cannot be negative";
  // parseFloat("15%") correctly reads 15.
  it('parses a percent-suffixed string value instead of misreading it as invalid', () => {
    const settings = { sparse_infill_density: '15%' } as unknown as OrcaProcessSettings;
    expect(validateOrcaPrintSettings(settings)).toEqual([]);
  });

  it('parses a percent-suffixed negative string value and rejects it', () => {
    const settings = { sparse_infill_density: '-15%' } as unknown as OrcaProcessSettings;
    const errors = validateOrcaPrintSettings(settings);
    expect(errors).toEqual([{ field: 'infillPercent', message: 'Infill density cannot be negative.' }]);
  });
});
