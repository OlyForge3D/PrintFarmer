import { useState } from 'react';
import { RotateCw } from 'lucide-react';
import { Printer, CameraHealthStatus } from '@/types/api';
import { CameraIcon, ExternalLinkIcon, ImageIcon, VideoIcon } from '@/common/components/icons/MdiIcons';
import { Button, Badge } from '@/common/components/ui';
import { usePrinterCameras } from '@/features/cameras/hooks/usePrinterCameras';
import {
  getCameraMediaTransformStyle,
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
  const mediaStyle = getCameraMediaTransformStyle(rotation);

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
            className="border-0 bg-black"
            style={mediaStyle}
            loading="lazy"
            referrerPolicy="no-referrer"
          />
        ) : hasCameraUrls && !imageError ? (
          <img
            src={activeUrl ?? ''}
            alt={`${p.name} camera feed`}
            className="object-cover"
            style={mediaStyle}
            loading="lazy"
            onError={() => setFailedUrl(activeUrl ?? '')}
          />
        ) : (
          <div className="absolute inset-0 flex flex-col items-center justify-center text-pf-text-tertiary p-4">
            <CameraIcon className="w-12 h-12 mb-2 opacity-30" />
            <span className="text-sm">{hasCameraUrls ? 'Camera unavailable' : 'No camera configured'}</span>
          </div>
        )}
        
        {/* Status overlay - top right */}
        <div className="absolute top-2 right-2 flex gap-1">
          {activeUrl && (
            <a
              href={cameraMode === 'stream' && hasStream ? cameraStreamUrl ?? activeUrl : activeUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-white/10 bg-black/40 text-white backdrop-blur-xs transition hover:border-white/20 hover:bg-black/60"
              title={`Open ${p.name} camera in a new tab`}
              aria-label={`Open ${p.name} camera in a new tab`}
            >
              <ExternalLinkIcon className="w-4 h-4" />
            </a>
          )}
          <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium backdrop-blur-xs ${
            isOnline 
              ? 'bg-pf-status-online-bg/80 text-pf-status-online-text' 
              : 'bg-pf-border-medium/80 text-pf-text-secondary'
          }`}>
            {isOnline ? 'Online' : 'Offline'}
          </span>
          {isPrinting && (
            <span className="inline-flex items-center px-2 py-0.5 rounded-sm text-xs font-medium bg-pf-warning/80 text-pf-text-primary backdrop-blur-xs">
              Printing
            </span>
          )}
        </div>

        {/* Camera health indicator & count - top left */}
        {primaryCamera && (
          <div className="absolute top-2 left-2 flex items-center gap-1.5">
            <span 
              className={`w-2.5 h-2.5 rounded-full ${getHealthDotColor(primaryCamera.healthStatus)}`}
              title={`Camera health: ${primaryCamera.healthStatus}`}
            />
            {cameraCount > 1 && (
              <Badge variant="default" size="sm" className="backdrop-blur-sm bg-pf-bg-1/80">
                {cameraCount} cameras
              </Badge>
            )}
          </div>
        )}

        <div className="absolute bottom-2 left-2">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={rotateClockwise}
            className="h-8 w-8 rounded-full border border-white/10 bg-black/50 p-0 text-white backdrop-blur-xs enabled:hover:bg-black/70"
            title="Rotate camera clockwise"
            aria-label="Rotate camera clockwise"
            iconCenter={<RotateCw className="w-4 h-4" />}
          />
        </div>
        {/* Camera mode toggle - bottom right (only if both modes available) */}
        {hasModeToggle && (
          <div className="absolute bottom-2 right-2 flex gap-1 bg-black/50 backdrop-blur-xs rounded-sm p-1">
            <Button
              type="button"
              variant={cameraMode === 'snapshot' ? 'primary' : 'subtle'}
              size="sm"
              onClick={() => setCameraMode('snapshot')}
              className="!p-1 !h-auto"
              title="Snapshot"
              aria-label="Snapshot mode"
              iconCenter={<ImageIcon className="w-4 h-4" />}
            />
            <Button
              type="button"
              variant={cameraMode === 'stream' ? 'primary' : 'subtle'}
              size="sm"
              onClick={() => setCameraMode('stream')}
              className="!p-1 !h-auto"
              title="Stream"
              aria-label="Stream mode"
              iconCenter={<VideoIcon className="w-4 h-4" />}
            />
          </div>
        )}

        {/* Camera indicator - only if we don't have camera data yet */}
        {hasCameraUrls && !imageError && !primaryCamera && (
          <div className="absolute top-2 left-2">
            <CameraIcon className="w-5 h-5 text-white/70 drop-shadow-sm" />
          </div>
        )}
      </div>

      {/* Footer - printer name and info */}
      <div className="p-3">
        <div className="font-bold text-base text-pf-text-primary font-bebas uppercase truncate">
          {p.name}
        </div>
        {p.modelName && (
          <div className="text-pf-text-secondary text-xs truncate">
            {p.modelName}
          </div>
        )}
      </div>
    </div>
  );
}
