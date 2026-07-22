import {
  CameraAccessMode,
  CameraSnapshotStrategy,
  CameraStreamFormat,
} from '@/types/api';

interface CameraPreviewContract {
  accessMode?: CameraAccessMode;
  streamFormat?: CameraStreamFormat;
  snapshotStrategy?: CameraSnapshotStrategy;
  snapshotUrl?: string | null;
}

export function shouldPollPrinterSnapshot({
  accessMode,
  snapshotStrategy,
  snapshotUrl,
}: CameraPreviewContract): boolean {
  if (snapshotStrategy === CameraSnapshotStrategy.SnapmakerU1MonitorJpeg) {
    return true;
  }

  if (
    snapshotStrategy === CameraSnapshotStrategy.DirectUrl ||
    snapshotStrategy === CameraSnapshotStrategy.None
  ) {
    return false;
  }

  return accessMode === CameraAccessMode.SnapshotOnly && !snapshotUrl;
}

export function isUnsupportedCameraPreview({
  accessMode,
  streamFormat,
  snapshotStrategy,
  snapshotUrl,
}: CameraPreviewContract): boolean {
  if (shouldPollPrinterSnapshot({ accessMode, snapshotStrategy, snapshotUrl })) {
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
  snapshotUrl,
}: CameraPreviewContract): boolean {
  if (shouldPollPrinterSnapshot({ accessMode, snapshotStrategy, snapshotUrl })) {
    return false;
  }

  if (isUnsupportedCameraPreview({ accessMode, streamFormat, snapshotStrategy, snapshotUrl })) {
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
