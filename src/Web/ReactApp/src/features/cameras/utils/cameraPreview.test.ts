import { describe, expect, it } from 'vitest';
import {
  CameraAccessMode,
  CameraSnapshotStrategy,
  CameraStreamFormat,
} from '@/types/api';
import {
  canUseMjpegStream,
  isUnsupportedCameraPreview,
  shouldPollPrinterSnapshot,
} from '@/features/cameras/utils/cameraPreview';

describe('cameraPreview', () => {
  it('polls authenticated same-origin printer camera proxies', () => {
    const contract = {
      accessMode: CameraAccessMode.Unknown,
      streamFormat: CameraStreamFormat.Unknown,
      snapshotStrategy: CameraSnapshotStrategy.None,
      snapshotUrl: '/api/printers/printer-1/camera/snapshot',
    };

    expect(shouldPollPrinterSnapshot(contract)).toBe(true);
    expect(isUnsupportedCameraPreview(contract)).toBe(false);
    expect(canUseMjpegStream(contract)).toBe(false);
  });

  it('leaves public direct snapshot URLs in direct-browser mode', () => {
    const contract = {
      accessMode: CameraAccessMode.SnapshotOnly,
      streamFormat: CameraStreamFormat.Jpeg,
      snapshotStrategy: CameraSnapshotStrategy.DirectUrl,
      snapshotUrl: 'http://camera.local/snapshot.jpg',
    };

    expect(shouldPollPrinterSnapshot(contract)).toBe(false);
    expect(isUnsupportedCameraPreview(contract)).toBe(false);
  });
});
