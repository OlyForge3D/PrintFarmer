import { useState } from 'react';
import { RotateCw } from 'lucide-react';
import { Printer, CameraHealthStatus } from '@/types/api';
import { CameraIcon, ExternalLinkIcon, ImageIcon, VideoIcon } from '@/common/components/icons/MdiIcons';
import { Button, Badge } from '@/common/components/ui';
import { usePrinterCameras } from '@/features/cameras/hooks/usePrinterCameras';
import {
  getCameraMediaTransformClassName,
  useCameraViewPreferences,
} from '@/features/cameras/hooks/useCameraViewPreferences';

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

  // Camera URL handling
  const cameraSnapshotUrl = p.cameraSnapshotUrl;
  const cameraStreamUrl = p.cameraStreamUrl;
  const hasCameraUrls = !!(cameraSnapshotUrl || cameraStreamUrl);
  const hasSnapshot = !!cameraSnapshotUrl;
  const hasStream = !!cameraStreamUrl;
  const {
    cameraMode,
    setCameraMode,
    rotation,
    rotateClockwise,
    hasModeToggle,
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

  // Determine which URL to show
  const activeUrl = cameraMode === 'stream' && hasStream 
    ? cameraStreamUrl 
    : cameraMode === 'snapshot' && hasSnapshot
    ? cameraSnapshotUrl
    : hasStream 
    ? cameraStreamUrl 
    : cameraSnapshotUrl;
  const imageError = !!activeUrl && failedUrl === activeUrl;
  const mediaClassName = getCameraMediaTransformClassName(rotation);

  // Health status dot color
  const getHealthDotColor = (health: CameraHealthStatus) => {
    switch (health) {
      case CameraHealthStatus.Healthy: return 'bg-pf-success';
      case CameraHealthStatus.Degraded: return 'bg-pf-warning';
      case CameraHealthStatus.Unhealthy: return 'bg-pf-error';
      default: return 'bg-pf-text-tertiary';
    }
  };

  return (
    <div className="rounded-xl shadow-lg backdrop-blur-xl bg-pf-bg-0/5 border border-white/10 hover:border-white/20 transition-colors overflow-hidden flex flex-col min-h-0">
      {/* Camera feed - main content */}
      <div className="relative w-full aspect-video bg-pf-bg-2">
        {cameraMode === 'stream' && hasStream && activeUrl ? (
          <iframe
            src={activeUrl}
            title={`${p.name} live camera feed`}
            className={`border-0 bg-black ${mediaClassName}`}
            loading="lazy"
            referrerPolicy="no-referrer"
          />
        ) : hasCameraUrls && !imageError ? (
          <img
            src={activeUrl ?? ''}
            alt={`${p.name} camera feed`}
            className={`object-cover ${mediaClassName}`}
            loading="lazy"
            onError={() => setFailedUrl(activeUrl ?? '')}
          />
        ) : (
          <div className="absolute inset-0 flex flex-col items-center justify-center text-pf-text-tertiary p-4">
            <CameraIcon className="w-12 h-12 mb-2 opacity-30" />
            <span className="text-sm">{hasCameraUrls ? 'Camera unavailable' : 'No camera configured'}</span>
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
            <span className="inline-flex items-center gap-1.5 rounded-full bg-pf-bg-2 px-2 py-1 text-[11px] text-pf-text-secondary">
              <span
                className={`h-2 w-2 rounded-full ${getHealthDotColor(primaryCamera.healthStatus)}`}
                title={`Camera health: ${primaryCamera.healthStatus}`}
              />
              <span>{primaryCamera.healthStatus}</span>
            </span>
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
            title={cameraMode === 'stream' ? 'Live stream active' : 'Snapshot preview active'}
          >
            <span className="sr-only">{cameraMode === 'stream' ? 'Live stream active' : 'Snapshot preview active'}</span>
            <span className="relative inline-flex items-center justify-center">
              {cameraMode === 'stream' ? (
                <VideoIcon className="w-4 h-4" />
              ) : (
                <ImageIcon className="w-4 h-4" />
              )}
              <span
                className={`absolute -right-0.5 -top-0.5 h-1.5 w-1.5 rounded-full ${cameraMode === 'stream' ? 'bg-pf-success' : 'bg-pf-accent'}`}
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
            {hasModeToggle && (
              <div className="flex gap-1 rounded-full border border-pf-border bg-pf-bg-2 p-1">
                <Button
                  type="button"
                  variant={cameraMode === 'snapshot' ? 'primary' : 'ghost'}
                  size="sm"
                  onClick={() => setCameraMode('snapshot')}
                  className="h-8 w-8 rounded-full p-0"
                  title="Snapshot"
                  aria-label="Snapshot mode"
                  iconCenter={<ImageIcon className="w-4 h-4" />}
                />
                <Button
                  type="button"
                  variant={cameraMode === 'stream' ? 'primary' : 'ghost'}
                  size="sm"
                  onClick={() => setCameraMode('stream')}
                  className="h-8 w-8 rounded-full p-0"
                  title="Stream"
                  aria-label="Stream mode"
                  iconCenter={<VideoIcon className="w-4 h-4" />}
                />
              </div>
            )}
            {activeUrl && (
              <a
                href={cameraMode === 'stream' && hasStream ? cameraStreamUrl ?? activeUrl : activeUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-full border border-pf-border bg-pf-bg-2 text-pf-text-primary transition hover:border-pf-border-strong hover:bg-pf-bg-3"
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
