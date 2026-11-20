import { describe, expect, it } from 'vitest';
import {
  getFilamentBedContext,
  getFilamentEditorBedContext,
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

  describe('filament editor resolved plate', () => {
    const resolved = { machineResolved: true, processResolved: true, projectMayOverride: false };
    const context = (machine: Record<string, unknown> | undefined, process: Record<string, unknown> | undefined,
      options = resolved, override = '', manufacturer = 'Qidi') =>
      getFilamentEditorBedContext(machine, process, override, manufacturer, options);

    it('uses Orca defaults for a fully resolved single-bed machine', () => {
      expect(getSingleBedTemperatureKey(context({}, {}))).toBe('cool_plate_temp');
    });

    it('uses the emitted process plate before the machine plate, and an override before both', () => {
      const machine = { curr_bed_type: 'Cool Plate' };
      const process = { curr_bed_type: 'Textured PEI Plate' };
      expect(getSingleBedTemperatureKey(context(machine, process))).toBe('textured_plate_temp');
      expect(getSingleBedTemperatureKey(context(machine, process, resolved, 'High Temp Plate'))).toBe('hot_plate_temp');
    });

    it('does not infer defaults from incomplete profiles or an overriding 3MF', () => {
      for (const options of [
        { ...resolved, machineResolved: false },
        { ...resolved, processResolved: false },
        { ...resolved, projectMayOverride: true },
      ]) {
        expect(getSingleBedTemperatureKey(context({ curr_bed_type: 'Cool Plate' }, {}, options))).toBeUndefined();
      }
    });

    it('keeps all plates for Bambu, multi-bed and unrecognized plate or flag', () => {
      expect(getSingleBedTemperatureKey(context({}, {}, resolved, '', 'BBL'))).toBeUndefined();
      expect(getSingleBedTemperatureKey(context({ support_multi_bed_types: '1' }, {}))).toBeUndefined();
      expect(getSingleBedTemperatureKey(context({ support_multi_bed_types: 'invalid' }, {}))).toBeUndefined();
      expect(getSingleBedTemperatureKey(context({}, { curr_bed_type: 'Unknown Plate' }))).toBeUndefined();
      expect(getSingleBedTemperatureKey(context({}, { curr_bed_type: null }))).toBeUndefined();
    });
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
