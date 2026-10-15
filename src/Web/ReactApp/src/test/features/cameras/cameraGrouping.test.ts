import { describe, expect, it } from 'vitest';
import {
  groupCamerasByLocation,
  groupCamerasByPrinter,
  parseCameraView,
} from '@/features/cameras/utils/cameraGrouping';
import { CameraHealthStatus, CameraSource, CameraType } from '@/types/api';
import type { DisplayCameraDto } from '@/types/api';

function camera(overrides: Partial<DisplayCameraDto> & Pick<DisplayCameraDto, 'id' | 'name'>): DisplayCameraDto {
  return {
    isEnabled: true,
    sortOrder: 0,
    isStandalone: true,
    printerIds: [],
    printers: [],
    isLocationDerived: false,
    source: CameraSource.Standalone,
    cameraType: CameraType.General,
    healthStatus: CameraHealthStatus.Healthy,
    ...overrides,
  };
}

describe('parseCameraView', () => {
  it.each([
    [null, 'all'],
    ['', 'all'],
    ['bogus', 'all'],
    ['all', 'all'],
    ['location', 'location'],
    ['printer', 'printer'],
  ] as const)('maps %s to %s', (input, expected) => {
    expect(parseCameraView(input)).toBe(expected);
  });
});

describe('groupCamerasByLocation', () => {
  it('groups by effective location, sorted by name, with a trailing Unassigned group', () => {
    const groups = groupCamerasByLocation([
      camera({ id: 'c1', name: 'Loose', effectiveLocationId: null }),
      camera({ id: 'c2', name: 'Shop A', effectiveLocationId: 'loc-w', effectiveLocationName: 'Workshop' }),
      camera({ id: 'c3', name: 'Office cam', effectiveLocationId: 'loc-o', effectiveLocationName: 'Office' }),
      camera({ id: 'c4', name: 'Shop B', effectiveLocationId: 'loc-w', effectiveLocationName: 'Workshop' }),
    ]);

    expect(groups.map((group) => [group.label, group.cameras.map((c) => c.id)])).toEqual([
      ['Office', ['c3']],
      ['Workshop', ['c2', 'c4']],
      ['Unassigned', ['c1']],
    ]);
  });

  it('omits the Unassigned group when every camera has a location', () => {
    const groups = groupCamerasByLocation([
      camera({ id: 'c1', name: 'A', effectiveLocationId: 'loc', effectiveLocationName: 'Lab' }),
    ]);
    expect(groups.map((group) => group.label)).toEqual(['Lab']);
  });
});

describe('groupCamerasByPrinter', () => {
  it('lists a multi-printer camera under each printer and unlinked cameras last', () => {
    const shared = camera({
      id: 'shared',
      name: 'Room cam',
      printers: [
        { id: 'p2', name: 'Voron', isPrimary: true },
        { id: 'p1', name: 'Bambu', isPrimary: false },
      ],
    });
    const groups = groupCamerasByPrinter([
      camera({ id: 'solo', name: 'Bambu nozzle', printers: [{ id: 'p1', name: 'Bambu', isPrimary: true }] }),
      shared,
      camera({ id: 'webcam', name: 'Webcam' }),
    ]);

    expect(groups.map((group) => [group.label, group.cameras.map((c) => c.id)])).toEqual([
      ['Bambu', ['solo', 'shared']],
      ['Voron', ['shared']],
      ['Not linked to a printer', ['webcam']],
    ]);
  });

  it('treats a camera without a printers array as unlinked', () => {
    const legacy = { ...camera({ id: 'legacy', name: 'Legacy' }), printers: undefined } as unknown as DisplayCameraDto;
    expect(groupCamerasByPrinter([legacy]).map((group) => group.label)).toEqual(['Not linked to a printer']);
  });
});
