import { describe, expect, it } from 'vitest';
import { CameraHealthStatus, CameraSource, CameraType } from '@/types/api';
import type { DisplayCameraDto } from '@/types/api';
import { isCameraOffline, orderCamerasForDisplay } from './cameraDisplayOrder';

function camera(id: string, printerIds: string[] = []): DisplayCameraDto {
  return {
    id,
    name: id,
    isEnabled: true,
    sortOrder: 0,
    isStandalone: printerIds.length === 0,
    printerIds,
    printers: [],
    isLocationDerived: false,
    source: CameraSource.Standalone,
    cameraType: CameraType.General,
    healthStatus: CameraHealthStatus.Healthy,
  };
}

const ids = (cameras: DisplayCameraDto[]) => cameras.map((c) => c.id);

describe('orderCamerasForDisplay', () => {
  it('puts online delivering cameras before online failed or unloaded cameras, then offline cameras', () => {
    const cameras = [
      camera('offline-delivering', ['p-off']),
      camera('online-failed', ['p-on']),
      camera('online-unloaded', ['p-on']),
      camera('online-delivering', ['p-on']),
      camera('standalone-delivering'),
    ];
    const printers = new Map([['p-on', true], ['p-off', false]]);

    expect(ids(orderCamerasForDisplay(cameras, {
      'offline-delivering': 'delivering',
      'online-failed': 'failed',
      'online-delivering': 'delivering',
      'standalone-delivering': 'delivering',
    }, printers))).toEqual([
      'online-delivering',
      'standalone-delivering',
      'online-failed',
      'online-unloaded',
      'offline-delivering',
    ]);
  });

  it('keeps the incoming order within a tier and does not mutate the input', () => {
    const cameras = [camera('c'), camera('a'), camera('b')];

    expect(ids(orderCamerasForDisplay(cameras, {}, null))).toEqual(['c', 'a', 'b']);
    expect(ids(orderCamerasForDisplay(cameras, { a: 'delivering', b: 'delivering' }, null))).toEqual(['a', 'b', 'c']);
    expect(ids(cameras)).toEqual(['c', 'a', 'b']);
  });
});

describe('isCameraOffline', () => {
  it('demotes only cameras whose linked printers are all known offline', () => {
    const printers = new Map([['p-on', true], ['p-off', false]]);

    expect(isCameraOffline(camera('x', ['p-off']), printers)).toBe(true);
    expect(isCameraOffline(camera('x', ['p-off', 'p-on']), printers)).toBe(false);
    expect(isCameraOffline(camera('x', ['p-off', 'p-missing']), printers)).toBe(false);
    expect(isCameraOffline(camera('x'), printers)).toBe(false);
    expect(isCameraOffline(camera('x', ['p-off']), null)).toBe(false);
  });

  it('falls back to the primary printer id when printerIds is empty', () => {
    expect(isCameraOffline({ ...camera('x'), printerId: 'p-off' }, new Map([['p-off', false]]))).toBe(true);
  });
});
