import type { ReactNode } from 'react';
import { useState } from 'react';
import clsx from 'clsx';
import { RotateCw } from 'lucide-react';
import { Button } from '@/common/components/ui';
import { CameraIcon, ExternalLinkIcon, ImageIcon, VideoIcon } from '@/common/components/icons/MdiIcons';
import {
  getCameraMediaTransformClassName,
  useCameraViewPreferences,
} from '@/features/cameras/hooks/useCameraViewPreferences';
import { usePrinterSnapshotPreview } from '@/features/cameras/hooks/usePrinterSnapshotPreview';
import {
  canUseMjpegStream,
  isUnsupportedCameraPreview,
  shouldPollPrinterSnapshot,
} from '@/features/cameras/utils/cameraPreview';
import type {
  CameraAccessMode,
  CameraSnapshotStrategy,
  CameraStreamFormat,
} from '@/types/api';

const ACTIVE_PREVIEW_REFRESH_MS = 4_000;
const IDLE_PREVIEW_REFRESH_MS = 12_000;

interface PrinterCameraPreviewProps {
  printerId: string;
  printerName: string;
  cameraStreamUrl?: string | null;
  cameraSnapshotUrl?: string | null;
  cameraAccessMode?: CameraAccessMode;
  cameraStreamFormat?: CameraStreamFormat;
  cameraSnapshotStrategy?: CameraSnapshotStrategy;
  isPrinting?: boolean;
  className?: string;
  overlay?: ReactNode;
}

export function PrinterCameraPreview({
  printerId,
  printerName,
  cameraStreamUrl,
  cameraSnapshotUrl,
  cameraAccessMode,
  cameraStreamFormat,
  cameraSnapshotStrategy,
  isPrinting = false,
  className,
  overlay,
}: PrinterCameraPreviewProps) {
  const previewContract = {
    accessMode: cameraAccessMode,
    streamFormat: cameraStreamFormat,
    snapshotStrategy: cameraSnapshotStrategy,
    snapshotUrl: cameraSnapshotUrl,
  };
  const pollSnapshotPreview = shouldPollPrinterSnapshot(previewContract);
  const unsupportedPreview = isUnsupportedCameraPreview(previewContract);
  const streamSupported = canUseMjpegStream(previewContract);
  const hasStream = !!cameraStreamUrl && streamSupported;
  const hasCameraSource = unsupportedPreview || hasStream || pollSnapshotPreview || !!cameraSnapshotUrl;
  const refreshIntervalMs = isPrinting ? ACTIVE_PREVIEW_REFRESH_MS : IDLE_PREVIEW_REFRESH_MS;
  const rawSnapshotKey = `${printerId}:${cameraSnapshotUrl ?? ''}`;
  const rawStreamKey = `${printerId}:${cameraStreamUrl ?? ''}`;
  const [failedRawSnapshotKey, setFailedRawSnapshotKey] = useState<string | null>(null);
  const [failedRawStreamKey, setFailedRawStreamKey] = useState<string | null>(null);
  const directSnapshotUrl = pollSnapshotPreview || failedRawSnapshotKey === rawSnapshotKey
    ? null
    : (cameraSnapshotUrl ?? null);
  const { previewContainerRef, snapshotSrc, snapshotFailed, isPollingPaused } = usePrinterSnapshotPreview(
    printerId,
    pollSnapshotPreview,
    refreshIntervalMs,
    directSnapshotUrl,
    !!directSnapshotUrl
  );
  const previewSrc = snapshotSrc ?? directSnapshotUrl;
  const hasSnapshot = pollSnapshotPreview || !!directSnapshotUrl;
  const {
    cameraMode,
    setCameraMode,
    rotation,
    rotateClockwise,
    hasModeToggle,
    hasMedia,
  } = useCameraViewPreferences({
    preferenceKey: `printer:${printerId}`,
    defaultMode: hasStream ? 'stream' : 'snapshot',
    hasStream,
    hasSnapshot,
  });
  const externalUrl = hasStream
    ? cameraStreamUrl
    : pollSnapshotPreview
    ? null
    : cameraSnapshotUrl ?? null;
  const mediaClassName = getCameraMediaTransformClassName(rotation);
  const showLiveStream = cameraMode === 'stream' && hasStream;
  const shouldUseImageStream = showLiveStream && failedRawStreamKey !== rawStreamKey;
  // CameraContractClassifier emits UnsupportedStream only when no snapshot exists; keep the snapshot branch defensive.
  const placeholderTitle = unsupportedPreview
    ? 'No live preview available'
    : hasCameraSource
    ? (showLiveStream ? 'Live stream unavailable' : 'Camera preview unavailable')
    : 'No linked camera configured';

  return (
    <div
      className={clsx(
        'relative overflow-hidden rounded-xl border border-pf-border bg-pf-bg-2/40',
        className
      )}
    >
      <div
        ref={previewContainerRef}
        className="relative aspect-video w-full overflow-hidden bg-pf-bg-0"
      >
        {showLiveStream && cameraStreamUrl && shouldUseImageStream ? (
          <img
            src={cameraStreamUrl}
            alt={`${printerName} live camera feed`}
            className={`h-full w-full object-contain bg-black ${mediaClassName}`}
            loading="eager"
            onError={() => {
              setFailedRawStreamKey(rawStreamKey);
            }}
          />
        ) : previewSrc ? (
          <img
            src={previewSrc}
            alt={`${printerName} camera preview`}
            className={`h-full w-full object-contain bg-black ${mediaClassName}`}
            loading="lazy"
            onError={() => {
              if (snapshotSrc) {
                return;
              }

              setFailedRawSnapshotKey(rawSnapshotKey);
            }}
          />
        ) : unsupportedPreview ? (
          <div className="absolute inset-0 flex flex-col items-center justify-center px-4 text-center text-pf-text-secondary">
            <CameraIcon className="mb-2 h-8 w-8 opacity-45" />
            <p className="text-sm font-medium">{placeholderTitle}</p>
            <p className="mt-1 text-xs text-pf-text-tertiary">
              This camera does not provide an embeddable MJPEG live stream.
            </p>
          </div>
        ) : showLiveStream && cameraStreamUrl ? (
          <iframe
            src={cameraStreamUrl}
            title={`${printerName} live camera feed`}
            className={`h-full w-full border-0 bg-black ${mediaClassName}`}
            loading="lazy"
            referrerPolicy="no-referrer"
          />
        ) : (
          <div className="absolute inset-0 flex flex-col items-center justify-center px-4 text-center text-pf-text-secondary">
            <CameraIcon className="mb-2 h-8 w-8 opacity-45" />
            <p className="text-sm font-medium">
              {hasCameraSource
                ? (showLiveStream ? 'Live stream unavailable' : 'Camera preview unavailable')
                : 'No linked camera configured'}
            </p>
            {hasCameraSource && (
              <p className="mt-1 text-xs text-pf-text-tertiary">
                {snapshotFailed
                  ? 'Snapshot polling is temporarily unavailable; the preview will retry automatically.'
                  : isPollingPaused
                  ? 'Snapshot polling is paused while the page is hidden.'
                  : 'Use live mode for the embedded stream or open the feed in a new tab.'}
              </p>
            )}
          </div>
        )}

        {overlay && (
          <div className="absolute left-3 top-3 z-10 max-w-[calc(100%-6rem)]">
            {overlay}
          </div>
        )}
      </div>

      {hasCameraSource && (
        <div className="flex items-center justify-end gap-2 overflow-x-auto border-t border-pf-border bg-pf-bg-1/90 px-3 py-2">
          <div
            className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-pf-bg-2 text-pf-text-secondary"
            role="status"
            title={showLiveStream ? 'Live stream active' : 'Snapshot preview active'}
          >
            <span className="sr-only">{showLiveStream ? 'Live stream active' : 'Snapshot preview active'}</span>
            <span className="relative inline-flex items-center justify-center">
              {showLiveStream ? (
                <VideoIcon className="h-3.5 w-3.5" />
              ) : (
                <ImageIcon className="h-3.5 w-3.5" />
              )}
              <span
                className={clsx(
                  'absolute -right-0.5 -top-0.5 h-1.5 w-1.5 rounded-full',
                  showLiveStream ? 'bg-pf-success' : 'bg-pf-accent'
                )}
                aria-hidden="true"
              />
            </span>
          </div>

          <div className="flex shrink-0 items-center gap-2">
            {hasMedia && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={rotateClockwise}
                className="h-8 w-8 rounded-full p-0"
                title="Rotate camera clockwise"
                aria-label="Rotate camera clockwise"
                iconCenter={<RotateCw className="h-3.5 w-3.5" />}
              />
            )}

            {hasModeToggle && (
              <div className="flex gap-1 rounded-full border border-pf-border bg-pf-bg-2 p-1">
                <Button
                  type="button"
                  variant={cameraMode === 'snapshot' ? 'primary' : 'ghost'}
                  size="sm"
                  onClick={() => setCameraMode('snapshot')}
                  className="h-8 w-8 rounded-full p-0"
                  title="Show snapshot preview"
                  aria-label="Show snapshot preview"
                  iconCenter={<ImageIcon className="h-3.5 w-3.5" />}
                />
                <Button
                  type="button"
                  variant={cameraMode === 'stream' ? 'primary' : 'ghost'}
                  size="sm"
                  onClick={() => setCameraMode('stream')}
                  className="h-8 w-8 rounded-full p-0"
                  title="Show live stream"
                  aria-label="Show live stream"
                  iconCenter={<VideoIcon className="h-3.5 w-3.5" />}
                />
              </div>
            )}

            {externalUrl && (
              <a
                href={externalUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-full border border-pf-border bg-pf-bg-2 text-pf-text-primary transition hover:border-pf-border-strong hover:bg-pf-bg-3"
                title="Open camera feed in a new tab"
                aria-label={`Open ${printerName} camera feed in a new tab`}
              >
                <ExternalLinkIcon className="h-3.5 w-3.5" />
              </a>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
