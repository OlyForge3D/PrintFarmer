import { describe, expect, it } from 'vitest';
import {
  hydrateFilamentProfileSettings,
  serializeFilamentProfileSettings,
} from '@/features/slicer/components/filamentProfileEditorAdapter';

describe('filament profile editor wire settings', () => {
  const raw = {
    nozzle_temperature: ['200', '215'],
    filament_soluble: ['1', '0'],
    filament_start_gcode: ['M117 X,Y\nG1 X1,Y2', 'M117 E2'],
    filament_shrink: ['100%', '99%'],
    fan_min_speed: ['60.00', '70'],
    compatible_printers_condition: 'nozzle_diameter[0]==0.4',
    unknown_vector: ['keep', { nested: true }],
  };

  it('hydrates single-entry controls without flattening text and preserves every untouched wire value', () => {
    const editor = hydrateFilamentProfileSettings(raw);
    expect(editor).toEqual({
      ...raw,
      nozzle_temperature: '200',
      filament_soluble: '1',
      filament_start_gcode: 'M117 X,Y\nG1 X1,Y2',
      filament_shrink: '100%',
      fan_min_speed: '60.00,70',
    });
    expect(serializeFilamentProfileSettings(editor, editor, raw)).toEqual(raw);
  });

  it('serializes edited vectors as native string arrays and retains hidden extruder entries', () => {
    const original = hydrateFilamentProfileSettings(raw);
    const edited = {
      ...original,
      nozzle_temperature: 221,
      filament_soluble: false,
      filament_start_gcode: 'M117 NEW,X\nG1 Y2',
      filament_shrink: 98.5,
      fan_min_speed: '60.01,71',
    };
    expect(serializeFilamentProfileSettings(edited, original, raw)).toEqual({
      ...raw,
      nozzle_temperature: ['221', '215'],
      filament_soluble: ['0', '0'],
      filament_start_gcode: ['M117 NEW,X\nG1 Y2', 'M117 E2'],
      filament_shrink: ['98.5%', '99%'],
      fan_min_speed: ['60.01', '71'],
    });
    expect(raw.nozzle_temperature).toEqual(['200', '215']);
  });

  it('adds absent vectors and resets ownership without synthesizing other settings', () => {
    const original = hydrateFilamentProfileSettings({});
    expect(serializeFilamentProfileSettings({
      nozzle_temperature: 221, filament_soluble: true,
    }, original, {})).toEqual({
      nozzle_temperature: ['221'], filament_soluble: ['1'],
    });
    expect(serializeFilamentProfileSettings({}, { nozzle_temperature: '200' }, {
      nozzle_temperature: ['200'], unrelated: 'keep',
    })).toEqual({ unrelated: 'keep' });
  });

  it('preserves unknown fields and scalar conditions when another setting changes', () => {
    const original = hydrateFilamentProfileSettings(raw);
    expect(serializeFilamentProfileSettings({
      ...original, nozzle_temperature: 221,
    }, original, raw)).toEqual({ ...raw, nozzle_temperature: ['221', '215'] });
  });

  it('hydrates a legacy chamber temperature and preserves hidden extruder entries on edit', () => {
    const legacy = { chamber_temperatures: ['40', '45'], unrelated: ['keep'] };
    const original = hydrateFilamentProfileSettings(legacy);
    expect(original).toEqual({ chamber_temperature: '40', unrelated: ['keep'] });
    expect(serializeFilamentProfileSettings({
      ...original, chamber_temperature: 41,
    }, original, legacy)).toEqual({
      chamber_temperature: ['41', '45'], unrelated: ['keep'],
    });
    expect(legacy).toEqual({ chamber_temperatures: ['40', '45'], unrelated: ['keep'] });
  });

  it('prefers the canonical chamber temperature and removes a conflicting legacy alias on edit', () => {
    const conflicting = {
      chamber_temperature: ['40', '45'], chamber_temperatures: ['0', '5'],
    };
    const original = hydrateFilamentProfileSettings(conflicting);
    expect(original).toEqual({ chamber_temperature: '40' });
    expect(serializeFilamentProfileSettings({
      ...original, chamber_temperature: 41,
    }, original, conflicting)).toEqual({ chamber_temperature: ['41', '45'] });
    expect(conflicting.chamber_temperatures).toEqual(['0', '5']);
  });

  it('removes both chamber temperature spellings when resetting ownership', () => {
    const conflicting = {
      chamber_temperature: ['40'], chamber_temperatures: ['0'], unrelated: 'keep',
    };
    const original = hydrateFilamentProfileSettings(conflicting);
    expect(serializeFilamentProfileSettings({
      unrelated: 'keep',
    }, original, conflicting)).toEqual({ unrelated: 'keep' });
    const legacy = { chamber_temperatures: ['40'], unrelated: 'keep' };
    expect(serializeFilamentProfileSettings({
      unrelated: 'keep',
    }, hydrateFilamentProfileSettings(legacy), legacy)).toEqual({ unrelated: 'keep' });
  });

  it.each([
    { chamber_temperatures: ['40', '45'] },
    { chamber_temperature: ['40', '45'], chamber_temperatures: ['0', '5'] },
  ])('preserves untouched chamber wire values when saving or editing an unrelated field', (chamber) => {
    const untouched = { ...chamber, nozzle_temperature: ['200'] };
    const original = hydrateFilamentProfileSettings(untouched);
    expect(serializeFilamentProfileSettings(original, original, untouched)).toEqual(untouched);
    expect(serializeFilamentProfileSettings({
      ...original, nozzle_temperature: 201,
    }, original, untouched)).toEqual({ ...untouched, nozzle_temperature: ['201'] });
  });

  it.each([
    ['nozzle_temperature', 221.5],
    ['fan_min_speed', '60,NaN'],
    ['filament_shrink', 'not a percent'],
    ['filament_soluble', 'maybe'],
  ])('rejects invalid %s edits instead of uploading malformed vectors', (key, value) => {
    const original = hydrateFilamentProfileSettings(raw);
    expect(() => serializeFilamentProfileSettings({
      ...original, [key]: value,
    }, original, raw)).toThrow();
  });
});
