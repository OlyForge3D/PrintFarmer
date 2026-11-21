import { describe, expect, it } from 'vitest';
import {
  getFilamentBedContext,
  getSingleBedTemperatureKey, isBedTemperatureSettingVisible, resolveBedType,
} from '@/features/slicer/utils/bedSurfaceTemperature';

describe('native bed-surface profile values', () => {
  it('follows explicit override, then process, then machine with no arbitrary fallback', () => {
    const machine = { curr_bed_type: 'Textured PEI Plate' };
    const process = { curr_bed_type: 'Cool Plate' };
    expect(resolveBedType('High Temp Plate', process, machine)).toBe('High Temp Plate');
    expect(resolveBedType('', process, machine)).toBe('Cool Plate');
    expect(resolveBedType('', {}, machine)).toBe('Textured PEI Plate');
    expect(resolveBedType('', {}, {})).toBeUndefined();
  });
});

describe('machine-aware filament bed setting visibility', () => {
  it.each([false, '0', 0, 'false'])('uses active textured keys when single-bed flag is %j', flag => {
    const context = getFilamentBedContext({ support_multi_bed_types: flag, curr_bed_type: 'Textured PEI Plate' }, {}, '');
    expect(getSingleBedTemperatureKey(context)).toBe('textured_plate_temp');
    expect(isBedTemperatureSettingVisible('hot_plate_temp', context)).toBe(false);
    expect(isBedTemperatureSettingVisible('textured_plate_temp_initial_layer', context)).toBe(true);
    expect(isBedTemperatureSettingVisible('textured_plate_temp', context)).toBe(true);
    expect(isBedTemperatureSettingVisible('nozzle_temperature', context)).toBe(true);
  });

  it.each([true, '1', 1, 'true', undefined, 'invalid'])('keeps all surfaces for multi-bed or unknown flag %j', flag => {
    const context = getFilamentBedContext({ support_multi_bed_types: flag, curr_bed_type: 'Textured PEI Plate' }, {}, '');
    expect(getSingleBedTemperatureKey(context)).toBeUndefined();
    expect(isBedTemperatureSettingVisible('hot_plate_temp', context)).toBe(true);
  });

  it('keeps all surfaces without machine/plate context', () => {
    expect(isBedTemperatureSettingVisible('hot_plate_temp', undefined)).toBe(true);
    for (const plate of [undefined, 'Default Plate', 'Unsupported']) {
      expect(getSingleBedTemperatureKey({ supportMultiBedTypes: false, bedType: plate })).toBeUndefined();
    }
  });

  it.each(['BBL', 'Bambu', 'Bambu Lab', 'BambuLab', ' bBl ', ' BAMBU '])(
    'keeps all surfaces for Bambu bundle manufacturer %s despite a single-bed flag', manufacturer => {
      const context = getFilamentBedContext({
        support_multi_bed_types: '0', curr_bed_type: 'Textured PEI Plate',
      }, {}, '', manufacturer);
      expect(context.supportMultiBedTypes).toBe(true);
      expect(getSingleBedTemperatureKey(context)).toBeUndefined();
      expect(isBedTemperatureSettingVisible('hot_plate_temp', context)).toBe(true);
    },
  );

  it.each([undefined, '', '  '])('recognizes custom Bambu machine models without a manufacturer %j', manufacturer => {
    const context = getFilamentBedContext({
      printer_model: ' BAmBu Lab X1 Carbon', support_multi_bed_types: '0', curr_bed_type: 'Textured PEI Plate',
    }, {}, '', manufacturer);
    expect(context.supportMultiBedTypes).toBe(true);
    expect(isBedTemperatureSettingVisible('hot_plate_temp', context)).toBe(true);
  });

  it('does not exempt non-Bambu machines from the single-bed flag', () => {
    const context = getFilamentBedContext({
      printer_model: 'Micron', support_multi_bed_types: '0', curr_bed_type: 'Textured PEI Plate',
    }, {}, '', 'Voron');
    expect(context.supportMultiBedTypes).toBe(false);
    expect(isBedTemperatureSettingVisible('hot_plate_temp', context)).toBe(false);
  });

  it('uses resolved inherited machine values and never guesses missing inheritance', () => {
    const resolved = { inherits: 'Micron base', support_multi_bed_types: '0', curr_bed_type: 'Cool Plate' };
    expect(getSingleBedTemperatureKey(getFilamentBedContext(resolved, {}, ''))).toBe('cool_plate_temp');
    expect(getSingleBedTemperatureKey(getFilamentBedContext({ inherits: 'Micron base' }, {}, ''))).toBeUndefined();
  });
});
