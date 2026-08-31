import { describe, expect, it } from 'vitest';
import {
  BUILD_VOLUME_X_EDITOR_KEY,
  BUILD_VOLUME_Y_EDITOR_KEY,
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

  it('rejects a partial build-volume edit rather than fabricating the missing dimension', () => {
    const original = hydrateMachineProfileSettings(machineProfile());
    const edited = { ...original, [BUILD_VOLUME_X_EDITOR_KEY]: 300 };

    expect(() => serializeMachineProfileSettings(edited, original, {}))
      .toThrow('Build volume requires positive X and Y dimensions');
  });
});
