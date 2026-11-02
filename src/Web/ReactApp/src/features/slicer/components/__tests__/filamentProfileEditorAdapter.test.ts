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
