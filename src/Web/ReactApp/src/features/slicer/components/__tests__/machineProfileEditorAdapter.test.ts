import { describe, expect, it } from 'vitest';
import {
  BUILD_VOLUME_X_EDITOR_KEY,
  BUILD_VOLUME_Y_EDITOR_KEY,
  MACHINE_PROFILE_COMMON_SIMPLE_KEYS,
  MACHINE_PROFILE_SETTING_MAPPINGS,
  hydrateMachineProfileSettings,
  serializeMachineProfileSettings,
} from '@/features/slicer/components/machineProfileEditorAdapter';
import type { OrcaMachineProfile } from '@/services/slicerProfilesService';

function machineProfile(
  overrides: Partial<OrcaMachineProfile> = {},
): OrcaMachineProfile {
  return {
    name: 'Test machine',
    manufacturer: 'Test',
    ...overrides,
  };
}

describe('machineProfileEditorAdapter', () => {
  it('hydrates promoted machine values under their Orca keys without replacing raw values', () => {
    const settings = hydrateMachineProfileSettings(machineProfile({
      buildVolumeX: 250,
      buildVolumeY: 210,
      buildVolumeZ: 220,
      nozzleDiameter: 0.4,
      gcodeDialect: 'klipper',
      hasHeatedBed: true,
      maxHotendTemperature: 300,
      maxAccelerationX: 10_000,
      settings: {
        nozzle_diameter: ['0.6', '0.8'],
        raw_only_setting: { nested: true },
      },
    }));

    expect(settings).toMatchObject({
      printable_area: ['0x0', '250x0', '250x210', '0x210'],
      printable_height: '220',
      nozzle_diameter: ['0.6', '0.8'],
      gcode_flavor: 'klipper',
      has_heated_bed: '1',
      max_hotend_temp: '300',
      machine_max_acceleration_x: ['10000'],
      [BUILD_VOLUME_X_EDITOR_KEY]: 250,
      [BUILD_VOLUME_Y_EDITOR_KEY]: 210,
      raw_only_setting: { nested: true },
    });
  });

  it('serializes edited common fields with native Orca keys and wire shapes', () => {
    const original = hydrateMachineProfileSettings(machineProfile({
      buildVolumeX: 250,
      buildVolumeY: 210,
      nozzleDiameter: 0.4,
      startGcode: 'G28',
      settings: { raw_only_setting: ['preserve', 'verbatim'] },
    }));
    const edited = {
      ...original,
      [BUILD_VOLUME_X_EDITOR_KEY]: 300,
      nozzle_diameter: '0.6',
      machine_start_gcode: 'G28\nM117 Ready',
    };

    const serialized = serializeMachineProfileSettings(
      edited,
      original,
      { raw_only_setting: ['preserve', 'verbatim'] },
    );

    expect(serialized).toMatchObject({
      printable_area: ['0x0', '300x0', '300x210', '0x210'],
      nozzle_diameter: ['0.6'],
      machine_start_gcode: 'G28\nM117 Ready',
      raw_only_setting: ['preserve', 'verbatim'],
    });
    expect(serialized).not.toHaveProperty(BUILD_VOLUME_X_EDITOR_KEY);
    expect(serialized).not.toHaveProperty(BUILD_VOLUME_Y_EDITOR_KEY);
  });

  it('preserves non-zero printable-area origins when dimensions are edited', () => {
    const original = hydrateMachineProfileSettings(machineProfile({
      settings: {
        printable_area: ['50x30', '300x30', '300x300', '50x300'],
      },
    }));
    const edited = {
      ...original,
      [BUILD_VOLUME_Y_EDITOR_KEY]: 300,
    };

    expect(serializeMachineProfileSettings(
      edited,
      original,
      { printable_area: ['50x30', '300x30', '300x300', '50x300'] },
    ).printable_area).toEqual([
      '50x30',
      '300x30',
      '300x330',
      '50x330',
    ]);
  });

  it('scales every point of a non-rectangular printable-area polygon', () => {
    const raw = {
      printable_area: ['10x20', '210x20', '210x120', '110x80', '10x120'],
    };
    const original = hydrateMachineProfileSettings(machineProfile({ settings: raw }));
    const edited = {
      ...original,
      [BUILD_VOLUME_X_EDITOR_KEY]: 400,
      [BUILD_VOLUME_Y_EDITOR_KEY]: 200,
    };

    expect(serializeMachineProfileSettings(edited, original, raw).printable_area).toEqual([
      '10x20',
      '410x20',
      '410x220',
      '210x140',
      '10x220',
    ]);
  });

  it('does not materialize absent promoted settings from metadata defaults', () => {
    const original = hydrateMachineProfileSettings(machineProfile({
      settings: { raw_only_setting: 'kept' },
    }));

    expect(original).toEqual({ raw_only_setting: 'kept' });
    expect(serializeMachineProfileSettings(
      original,
      original,
      { raw_only_setting: 'kept' },
    )).toEqual({
      raw_only_setting: 'kept',
    });
  });

  it('does not materialize untouched promoted DTO values during an unrelated edit', () => {
    const raw = { raw_only_setting: 'kept' };
    const original = hydrateMachineProfileSettings(machineProfile({
      maxHotendTemperature: 300,
      settings: raw,
    }));
    const edited = { ...original, raw_only_setting: 'changed' };

    expect(serializeMachineProfileSettings(edited, original, raw)).toEqual({
      raw_only_setting: 'changed',
    });
  });

  it('normalizes promoted printable-area strings to native point arrays', () => {
    const settings = hydrateMachineProfileSettings(machineProfile({
      printableArea: '0x0, 250x0, 250x210, 0x210',
    }));

    expect(settings.printable_area).toEqual(['0x0', '250x0', '250x210', '0x210']);
  });

  it('hydrates scalar per-extruder DTO values as one supported Orca array entry', () => {
    const settings = hydrateMachineProfileSettings(machineProfile({
      nozzleDiameter: 0.4,
      retractionLength: 0.8,
    }));

    expect(settings.nozzle_diameter).toEqual(['0.4']);
    expect(settings.retraction_length).toEqual(['0.8']);
  });

  it('rejects empty or non-numeric per-extruder edits', () => {
    const original = hydrateMachineProfileSettings(machineProfile({
      nozzleDiameter: 0.4,
    }));

    expect(() => serializeMachineProfileSettings(
      { ...original, nozzle_diameter: '' },
      original,
      {},
    )).toThrow('Per-extruder machine settings require at least one numeric value.');
  });

  it('rejects unknown boolean edits instead of coercing them to false', () => {
    const original = hydrateMachineProfileSettings(machineProfile({
      hasHeatedBed: true,
    }));

    expect(() => serializeMachineProfileSettings(
      { ...original, has_heated_bed: 'unknown' },
      original,
      {},
    )).toThrow('Boolean machine settings must be true or false.');
  });

  it('serializes the promoted firmware-retraction checkbox as an Orca boolean string', () => {
    const raw = { use_firmware_retraction: '0' };
    const original = hydrateMachineProfileSettings(machineProfile({ settings: raw }));

    expect(serializeMachineProfileSettings(
      { ...original, use_firmware_retraction: true },
      original,
      raw,
    )).toEqual({ use_firmware_retraction: '1' });
  });

  it('preserves fallback aliases until their canonical control is explicitly edited', () => {
    const raw = {
      max_print_height: '220',
      bed_temperature_limit: '110',
      nozzle_temperature_range_high: ['300'],
      retract_length: ['0.8'],
      retract_speed: ['35'],
      deretract_speed: ['25'],
      machine_type: 'corexy',
      bed_custom_texture: 'texture.svg',
    };
    const original = hydrateMachineProfileSettings(machineProfile({
      buildVolumeZ: 220,
      maxBedTemperature: 110,
      maxHotendTemperature: 300,
      retractionLength: 0.8,
      retractionSpeed: 35,
      detractionSpeed: 25,
      motionType: 'corexy',
      hasHeatedBed: true,
      settings: raw,
    }));

    expect(serializeMachineProfileSettings(original, original, raw)).toEqual(raw);

    const serialized = serializeMachineProfileSettings({
      ...original,
      printable_height: 250,
      max_bed_temp: 120,
      max_hotend_temp: 320,
      retraction_length: 1,
      retraction_speed: 40,
      deretraction_speed: 30,
      printer_type: 'cartesian',
      has_heated_bed: false,
    }, original, raw);

    expect(serialized).toMatchObject({
      printable_height: '250',
      max_bed_temp: '120',
      max_hotend_temp: '320',
      retraction_length: ['1'],
      retraction_speed: ['40'],
      deretraction_speed: ['30'],
      printer_type: 'cartesian',
      has_heated_bed: '0',
      bed_custom_texture: 'texture.svg',
    });
    for (const alias of [
      'max_print_height',
      'bed_temperature_limit',
      'nozzle_temperature_range_high',
      'retract_length',
      'retract_speed',
      'deretract_speed',
      'machine_type',
    ]) {
      expect(serialized).not.toHaveProperty(alias);
    }
  });

  it('does not expose the provenance-losing retraction lift DTO as retract_lift_above', () => {
    const settings = hydrateMachineProfileSettings(machineProfile({
      retractionLiftZ: 2,
    }));

    expect(settings).not.toHaveProperty('retract_lift_above');
    expect(MACHINE_PROFILE_COMMON_SIMPLE_KEYS.has('retract_lift_above')).toBe(false);
  });

  it('has a wire-shape mapping for every non-pseudo Simple-mode promotion', () => {
    const mappedKeys = new Set(
      MACHINE_PROFILE_SETTING_MAPPINGS.map((mapping) => mapping.orcaKey),
    );
    const pseudoKeys = new Set([
      BUILD_VOLUME_X_EDITOR_KEY,
      BUILD_VOLUME_Y_EDITOR_KEY,
    ]);

    for (const key of MACHINE_PROFILE_COMMON_SIMPLE_KEYS) {
      if (!pseudoKeys.has(key)) {
        expect(mappedKeys.has(key), key).toBe(true);
      }
    }
  });

  it('rejects a partial build-volume edit rather than fabricating the missing dimension', () => {
    const original = hydrateMachineProfileSettings(machineProfile());
    const edited = { ...original, [BUILD_VOLUME_X_EDITOR_KEY]: 300 };

    expect(() => serializeMachineProfileSettings(edited, original, {}))
      .toThrow('Build volume requires positive X and Y dimensions');
  });
});
