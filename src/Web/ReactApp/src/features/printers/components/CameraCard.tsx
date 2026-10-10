import { useState } from 'react';
import { RotateCw } from 'lucide-react';
import type { Printer } from '@/types/api';
import { CameraIcon, ExternalLinkIcon, ImageIcon, VideoIcon } from '@/common/components/icons/MdiIcons';
import { Button, Badge } from '@/common/components/ui';
import { CameraModeControl } from '@/features/cameras/components/CameraModeControl';
import { CameraHealthBadge } from '@/features/cameras/components/CameraHealthBadge';
import { usePrinterCameras } from '@/features/cameras/hooks/usePrinterCameras';
import {
  getCameraMediaTransformClassName,
  useCameraViewPreferences,
} from '@/features/cameras/hooks/useCameraViewPreferences';
import { usePrinterSnapshotPreview } from '@/features/cameras/hooks/usePrinterSnapshotPreview';
import { useAuthenticatedMjpegStream } from '@/features/cameras/hooks/useAuthenticatedMjpegStream';
import { getAuthenticatedCameraProxyRoute } from '@/common/auth/authenticatedCameraRoutes';
import {
  canUseMjpegStream,
  isUnsupportedCameraPreview,
  shouldPollPrinterSnapshot,
} from '@/features/cameras/utils/cameraPreview';

const ACTIVE_PREVIEW_REFRESH_MS = 4_000;
const IDLE_PREVIEW_REFRESH_MS = 12_000;

interface CameraCardProps {
  printer: Printer;
}

/**
 * CameraCard - Displays a printer's camera feed in a card format
 * Used in the "Camera View" mode on the Printers page
 */
export function CameraCard({
  printer: p,
}: CameraCardProps) {
  const [failedUrl, setFailedUrl] = useState<string | null>(null);
  const isOnline = p.isOnline ?? false;
  const state = p.state ?? '';
  const isPrinting = state.toLowerCase().includes('printing');

  // Camera source handling
  const cameraSnapshotUrl = p.cameraSnapshotUrl;
  const cameraStreamUrl = p.cameraStreamUrl;
  const previewContract = {
    accessMode: p.cameraAccessMode,
    streamFormat: p.cameraStreamFormat,
    snapshotStrategy: p.cameraSnapshotStrategy,
    streamUrl: cameraStreamUrl,
    snapshotUrl: cameraSnapshotUrl,
  };
  const pollSnapshotPreview = shouldPollPrinterSnapshot(previewContract);
  const unsupportedPreview = isUnsupportedCameraPreview(previewContract);
  const hasStream = !!cameraStreamUrl && canUseMjpegStream(previewContract);
  const refreshIntervalMs = isPrinting ? ACTIVE_PREVIEW_REFRESH_MS : IDLE_PREVIEW_REFRESH_MS;
  const directSnapshotUrl = pollSnapshotPreview ? null : cameraSnapshotUrl;
  const { previewContainerRef, snapshotSrc, snapshotFailed } = usePrinterSnapshotPreview(
    p.id,
    pollSnapshotPreview,
    refreshIntervalMs,
    directSnapshotUrl,
    !!directSnapshotUrl,
    getAuthenticatedCameraProxyRoute(cameraSnapshotUrl)
  );
  const hasCameraUrls = unsupportedPreview || hasStream || pollSnapshotPreview || !!directSnapshotUrl;
  const hasSnapshot = pollSnapshotPreview || !!directSnapshotUrl;
  const {
    cameraMode,
    setCameraMode,
    rotation,
    rotateClockwise,
  } = useCameraViewPreferences({
    preferenceKey: `printer:${p.id}`,
    defaultMode: hasStream ? 'stream' : 'snapshot',
    hasStream,
    hasSnapshot,
  });

  // Fetch cameras for this printer to get health status
  const { data: printerCameras } = usePrinterCameras(p.id);
  const primaryCamera = printerCameras?.[0];
  const cameraCount = printerCameras?.length ?? 0;

  const { streamSrc, streamUnsupported, streamFailed } = useAuthenticatedMjpegStream(
    cameraStreamUrl,
    cameraMode === 'stream' && hasStream,
  );
  const safeStreamRoute = getAuthenticatedCameraProxyRoute(cameraStreamUrl);
  const liveStreamSrc = safeStreamRoute ? streamSrc : cameraStreamUrl;
  const snapshotPreviewUrl = snapshotSrc ?? directSnapshotUrl;
  const streamImageFailed = !!liveStreamSrc && failedUrl === liveStreamSrc;
  const streamFallsBackToSnapshot = cameraMode === 'stream' && (streamUnsupported || streamFailed || streamImageFailed) && !!snapshotPreviewUrl;
  const streamIssue = cameraMode === 'stream'
    ? streamUnsupported
      ? 'unsupported'
      : streamFailed || streamImageFailed
        ? 'failed'
        : undefined
    : undefined;
  const displayedMode = streamIssue && hasSnapshot ? 'snapshot' : cameraMode;
  const activeUrl = cameraMode === 'stream' && hasStream
    ? streamFallsBackToSnapshot ? snapshotPreviewUrl : liveStreamSrc
    : cameraMode === 'snapshot' && snapshotPreviewUrl
      ? snapshotPreviewUrl
      : hasStream
        ? liveStreamSrc
        : snapshotPreviewUrl;
  const showingLiveStream = cameraMode === 'stream' && !!liveStreamSrc && !streamUnsupported && !streamFailed && !streamImageFailed;
  const imageError = !!activeUrl && failedUrl === activeUrl;
  const mediaClassName = getCameraMediaTransformClassName(rotation);
  const externalUrl = activeUrl && !getAuthenticatedCameraProxyRoute(activeUrl) ? activeUrl : null;

  return (
    <div className="rounded-lg shadow-lg backdrop-blur-xl bg-pf-bg-0/5 border border-white/10 hover:border-white/20 transition-colors overflow-hidden flex flex-col min-h-0">
      {/* Camera feed - main content */}
      <div ref={previewContainerRef} className="relative w-full aspect-video bg-pf-bg-2">
        {activeUrl && !imageError ? (
          <img
            src={activeUrl ?? ''}
            alt={showingLiveStream ? `${p.name} live camera feed` : `${p.name} camera preview`}
            className={`object-contain bg-black ${mediaClassName}`}
            loading="lazy"
            onError={() => setFailedUrl(activeUrl ?? '')}
          />
        ) : unsupportedPreview ? (
          <div role="status" aria-live="polite" className="absolute inset-0 flex flex-col items-center justify-center text-pf-text-tertiary p-4">
            <CameraIcon className="w-12 h-12 mb-2 opacity-30" />
            <span className="text-center text-sm font-medium text-pf-text-secondary">No live preview available</span>
            <span className="mt-1 max-w-xs text-center text-xs text-pf-text-tertiary">
              This camera does not provide an embeddable MJPEG live stream.
            </span>
          </div>
        ) : (
          <div role="status" aria-live="polite" className="absolute inset-0 flex flex-col items-center justify-center text-pf-text-tertiary p-4">
            <CameraIcon className="w-12 h-12 mb-2 opacity-30" />
            <span className="text-sm">{streamUnsupported
              ? hasSnapshot ? 'Live preview unsupported; using snapshot preview' : 'Live preview unsupported; no snapshot configured'
              : streamFailed ? 'Live stream unavailable; reconnecting' : hasCameraUrls ? 'Camera unavailable' : 'No linked camera configured'}</span>
            {snapshotFailed && (
              <span className="mt-1 max-w-xs text-center text-xs text-pf-text-tertiary">
                Snapshot polling is temporarily unavailable; the preview will retry automatically.
              </span>
            )}
          </div>
        )}
      </div>

      {/* Footer - printer name and info */}
      <div className="space-y-3 p-3">
        <div className="font-bold text-base text-pf-text-primary font-bebas uppercase truncate">
          {p.name}
        </div>
        {p.modelName && (
          <div className="text-pf-text-secondary text-xs truncate">
            {p.modelName}
          </div>
        )}

        <div className="flex flex-wrap items-center gap-2">
          <Badge
            variant={isOnline ? 'success' : 'default'}
            size="sm"
          >
            {isOnline ? 'Online' : 'Offline'}
          </Badge>
          {isPrinting && (
            <Badge variant="warning" size="sm">
              Printing
            </Badge>
          )}
          {primaryCamera && (
            <CameraHealthBadge
              healthStatus={primaryCamera.healthStatus}
              previewFailed={imageError || snapshotFailed || streamUnsupported || streamFailed || streamImageFailed}
            />
          )}
          {cameraCount > 1 && (
            <Badge variant="default" size="sm">
              {cameraCount} cameras
            </Badge>
          )}
        </div>

        <div className="flex items-center justify-end gap-2 overflow-x-auto">
          <div
            className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-pf-bg-2 text-pf-text-secondary"
            role="status"
            title={streamIssue
              ? `${streamIssue === 'unsupported' ? 'Live stream unsupported' : 'Live stream unavailable'}${hasSnapshot ? ' · showing snapshot' : ''}`
              : displayedMode === 'stream' ? 'Live stream active' : 'Snapshot preview active'}
          >
            <span className="sr-only">
              {streamIssue
                ? `${streamIssue === 'unsupported' ? 'Live stream unsupported' : 'Live stream unavailable'}${hasSnapshot ? ' · showing snapshot' : ''}`
                : displayedMode === 'stream' ? 'Live stream active' : 'Snapshot preview active'}
            </span>
            <span className="relative inline-flex items-center justify-center">
              {displayedMode === 'stream' ? (
                <VideoIcon className="w-4 h-4" />
              ) : (
                <ImageIcon className="w-4 h-4" />
              )}
              <span
                className={`absolute -right-0.5 -top-0.5 h-1.5 w-1.5 rounded-full ${displayedMode === 'stream' && !streamIssue ? 'bg-pf-success' : 'bg-pf-accent'}`}
                aria-hidden="true"
              />
            </span>
          </div>

          <div className="flex shrink-0 items-center gap-2">
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={rotateClockwise}
              className="h-8 w-8 rounded-full p-0"
              title="Rotate camera clockwise"
              aria-label="Rotate camera clockwise"
              iconCenter={<RotateCw className="w-4 h-4" />}
            />
            <CameraModeControl
              cameraName={p.name}
              cameraMode={cameraMode}
              hasStream={hasStream}
              hasSnapshot={hasSnapshot}
              streamUnavailable={!!cameraStreamUrl && !hasStream}
              streamIssue={streamIssue}
              onModeChange={setCameraMode}
            />
            {externalUrl && (
              <a
                href={externalUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-full border border-pf-border bg-pf-bg-2 text-pf-text-primary transition hover:border-pf-border-strong hover:bg-pf-bg-1"
                title={`Open ${p.name} camera in a new tab`}
                aria-label={`Open ${p.name} camera in a new tab`}
              >
                <ExternalLinkIcon className="w-4 h-4" />
              </a>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
