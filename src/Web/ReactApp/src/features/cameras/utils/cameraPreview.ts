import {
  CameraAccessMode,
  CameraSnapshotStrategy,
  CameraStreamFormat,
} from '@/types/api';

interface CameraPreviewContract {
  accessMode?: CameraAccessMode;
  streamFormat?: CameraStreamFormat;
  snapshotStrategy?: CameraSnapshotStrategy;
}

export function shouldPollPrinterSnapshot({
  accessMode,
  snapshotStrategy,
}: CameraPreviewContract): boolean {
  return (
    accessMode === CameraAccessMode.SnapshotOnly ||
    snapshotStrategy === CameraSnapshotStrategy.SnapmakerU1MonitorJpeg
  );
}

export function isUnsupportedCameraPreview({
  accessMode,
  streamFormat,
  snapshotStrategy,
}: CameraPreviewContract): boolean {
  if (shouldPollPrinterSnapshot({ accessMode, snapshotStrategy })) {
    return false;
  }

  return (
    accessMode === CameraAccessMode.UnsupportedStream ||
    streamFormat === CameraStreamFormat.Unsupported
  );
}

export function canUseMjpegStream({
  accessMode,
  streamFormat,
  snapshotStrategy,
}: CameraPreviewContract): boolean {
  if (shouldPollPrinterSnapshot({ accessMode, snapshotStrategy })) {
    return false;
  }

  if (isUnsupportedCameraPreview({ accessMode, streamFormat })) {
    return false;
  }

  const streamModeSupported =
    accessMode === undefined ||
    accessMode === CameraAccessMode.Unknown ||
    accessMode === CameraAccessMode.StreamAndSnapshot ||
    accessMode === CameraAccessMode.StreamOnly;
  const streamFormatSupported =
    streamFormat === undefined ||
    streamFormat === CameraStreamFormat.Unknown ||
    streamFormat === CameraStreamFormat.Mjpeg;

  return streamModeSupported && streamFormatSupported;
}
