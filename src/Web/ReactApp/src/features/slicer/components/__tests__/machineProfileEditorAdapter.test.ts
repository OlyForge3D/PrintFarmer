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

    const serialized = serializeMachineProfileSettings(edited, original);

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

    expect(serializeMachineProfileSettings(edited, original).printable_area).toEqual([
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
    expect(serializeMachineProfileSettings(original, original)).toEqual({
      raw_only_setting: 'kept',
    });
  });

  it('rejects a partial build-volume edit rather than fabricating the missing dimension', () => {
    const original = hydrateMachineProfileSettings(machineProfile());
    const edited = { ...original, [BUILD_VOLUME_X_EDITOR_KEY]: 300 };

    expect(() => serializeMachineProfileSettings(edited, original))
      .toThrow('Build volume X and Y must both be greater than zero.');
  });
});
