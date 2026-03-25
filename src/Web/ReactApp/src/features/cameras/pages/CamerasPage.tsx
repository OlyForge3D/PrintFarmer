import { useMemo, useState, useEffect } from 'react';
import { RotateCw } from 'lucide-react';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Badge } from '@/common/components/ui';
import { CameraIcon, ExternalLinkIcon, ImageIcon, VideoIcon, SettingsIcon } from '@/common/components/icons/MdiIcons';
import { cameraService } from '@/services/cameraService';
import type { DisplayCameraDto, CameraSource, CameraType } from '@/types/api';
import { useSearchParams, useParams, useNavigate } from 'react-router';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { CameraManagementPanel } from '@/features/cameras/components/CameraManagementPanel';
import { CameraHealthBadge } from '@/features/cameras/components/CameraHealthBadge';
import {
  getCameraMediaTransformStyle,
  useCameraViewPreferences,
} from '@/features/cameras/hooks/useCameraViewPreferences';

/**
 * CamerasPage - Display all enabled cameras in a grid view
 * 
 * Shows cameras that have been explicitly added to the system via the Camera Admin page.
 * Supports both stream and snapshot modes for each camera.
 */
export function CamerasPage() {
  const auth = useAuth();
  const [cameras, setCameras] = useState<DisplayCameraDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [searchParams] = useSearchParams();
  const { tabId } = useParams<{ tabId?: string }>();
  const navigate = useNavigate();
  const canManageCameras = auth.hasRole('farm_admin');
  const activeTab = useMemo<'view' | 'manage'>(() => {
    if (tabId === 'manage' && canManageCameras) return 'manage';
    if (tabId === 'view') return 'view';
    const requestedTab = searchParams.get('tab');
    if (requestedTab === 'manage' && canManageCameras) return 'manage';
    return 'view';
  }, [canManageCameras, searchParams, tabId]);

  useEffect(() => {
    loadCameras();
  }, []);

  const loadCameras = async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await cameraService.getDisplayCameras();
      // Sort by sortOrder
      data.sort((a, b) => a.sortOrder - b.sortOrder);
      setCameras(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load cameras');
    } finally {
      setLoading(false);
    }
  };

  const setTab = (nextTab: 'view' | 'manage') => {
    navigate(`/cameras/${nextTab}`, { replace: true });
  };

  return (
    <PageTemplate
      title="Cameras"
      subtitle="Live camera feeds from your print farm"
      icon={CameraIcon}
      actions={
        <div className="flex items-center gap-2">
          {canManageCameras && (
            <Button
              variant="secondary"
              iconLeft={<SettingsIcon className="w-4 h-4" />}
              onClick={() => setTab(activeTab === 'manage' ? 'view' : 'manage')}
            >
              {activeTab === 'manage' ? 'View Cameras' : 'Manage'}
            </Button>
          )}

          {activeTab === 'view' && (
            <Button variant="secondary" onClick={loadCameras}>
              Refresh
            </Button>
          )}
        </div>
      }
    >
      {activeTab === 'manage' ? (
        <CameraManagementPanel onCamerasChanged={loadCameras} />
      ) : (
        <div className="space-y-6">
          {/* Error message */}
          {error && (
            <div className="px-4 py-3 rounded-sm bg-pf-error-bg border border-pf-error text-pf-error">
              {error}
            </div>
          )}

          {/* Loading state */}
          {loading ? (
            <div className="text-center py-12">
              <div className="pf-animate-skeleton text-pf-text-secondary">Loading cameras...</div>
            </div>
          ) : cameras.length === 0 ? (
            <div className="text-center py-12">
              <CameraIcon className="h-16 w-16 text-pf-text-tertiary mx-auto mb-4" />
              <h3 className="text-xl font-semibold text-pf-text-primary mb-2">No Cameras Configured</h3>
              <p className="text-pf-text-secondary mb-4 max-w-md mx-auto">
                Add cameras to monitor your print farm. You can add standalone webcams or import cameras from your printers.
              </p>
              {canManageCameras && (
                <Button iconLeft={<SettingsIcon className="w-4 h-4" />} onClick={() => setTab('manage')}>
                  Configure Cameras
                </Button>
              )}
              {!canManageCameras && (
                <p className="text-sm text-pf-text-tertiary max-w-md mx-auto">
                  Ask an administrator to configure cameras.
                </p>
              )}
            </div>
          ) : (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
              {cameras.map((camera) => (
                <CameraViewCard key={camera.id} camera={camera} />
              ))}
            </div>
          )}
        </div>
      )}
    </PageTemplate>
  );
}

interface CameraViewCardProps {
  camera: DisplayCameraDto;
}

const sourceLabels: Record<CameraSource, string> = {
  Standalone: 'Standalone',
  Moonraker: 'Moonraker',
  PrusaLink: 'PrusaLink',
  OctoPrint: 'OctoPrint',
  SDCP: 'SDCP',
  FlashForge: 'FlashForge',
};

const cameraTypeLabels: Record<CameraType, string> = {
  General: 'General',
  Bed: 'Bed',
  Nozzle: 'Nozzle',
  Wide: 'Wide',
  Timelapse: 'Timelapse',
};

/**
 * CameraViewCard - Individual camera feed card
 */
function CameraViewCard({ camera }: CameraViewCardProps) {
  const [failedUrl, setFailedUrl] = useState<string | null>(null);

  const hasSnapshot = !!camera.snapshotUrl;
  const hasStream = !!camera.streamUrl;
  const {
    cameraMode,
    setCameraMode,
    rotation,
    rotateClockwise,
    hasModeToggle,
  } = useCameraViewPreferences({
    preferenceKey: `camera:${camera.id}`,
    defaultMode: hasStream ? 'stream' : 'snapshot',
    hasStream,
    hasSnapshot,
  });

  // Determine which URL to show
  const activeUrl = cameraMode === 'stream' && hasStream
    ? camera.streamUrl
    : cameraMode === 'snapshot' && hasSnapshot
    ? camera.snapshotUrl
    : hasStream
    ? camera.streamUrl
    : camera.snapshotUrl;
  const imageError = !!activeUrl && failedUrl === activeUrl;
  const mediaStyle = getCameraMediaTransformStyle(rotation);

  return (
    <div className="rounded-xl shadow-lg backdrop-blur-xl bg-pf-bg-0/5 border border-white/10 hover:border-white/20 transition-colors overflow-hidden flex flex-col">
      {/* Camera feed */}
      <div className="relative w-full aspect-video bg-pf-bg-2">
        {cameraMode === 'stream' && hasStream && activeUrl ? (
          <iframe
            src={activeUrl}
            title={`${camera.name} live camera feed`}
            className="border-0 bg-black"
            style={mediaStyle}
            loading="lazy"
            referrerPolicy="no-referrer"
          />
        ) : activeUrl && !imageError ? (
          <img
            src={activeUrl}
            alt={`${camera.name} camera feed`}
            className="object-cover"
            style={mediaStyle}
            loading="lazy"
            onError={() => setFailedUrl(activeUrl ?? '')}
          />
        ) : (
          <div className="absolute inset-0 flex flex-col items-center justify-center text-pf-text-tertiary p-4">
            <CameraIcon className="w-12 h-12 mb-2 opacity-30" />
            <span className="text-sm">
              {imageError
                ? cameraMode === 'snapshot' ? 'Snapshot unavailable' : 'Camera unavailable'
                : 'No feed URL'}
            </span>
          </div>
        )}

        {/* Health status - top left */}
        <div className="absolute top-2 left-2">
          <CameraHealthBadge healthStatus={camera.healthStatus} size="sm" />
        </div>

        {/* Source badge - top right */}
        <div className="absolute top-2 right-2 flex items-center gap-1">
          <Badge variant="default" size="sm" className="backdrop-blur-sm bg-pf-bg-1/80">
            {sourceLabels[camera.source]}
          </Badge>
          {activeUrl && (
            <a
              href={cameraMode === 'stream' && hasStream ? camera.streamUrl : activeUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-white/10 bg-pf-bg-1/80 text-pf-text-primary backdrop-blur-sm transition hover:border-white/20 hover:bg-pf-bg-1"
              title={`Open ${camera.name} in a new tab`}
              aria-label={`Open ${camera.name} in a new tab`}
            >
              <ExternalLinkIcon className="w-4 h-4" />
            </a>
          )}
        </div>

        {/* Camera controls */}
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
      </div>

      {/* Camera info */}
      <div className="p-3 bg-pf-bg-1">
        <div className="flex items-center gap-2 mb-1">
          <CameraIcon className="w-4 h-4 text-pf-text-tertiary shrink-0" />
          <div className="min-w-0 flex-1">
            <h3 className="font-medium text-pf-text-primary truncate">{camera.name}</h3>
            {camera.printerName && (
              <p className="text-xs text-pf-text-secondary truncate">
                Printer: {camera.printerName}
              </p>
            )}
            {camera.location && (
              <p className="text-xs text-pf-text-tertiary truncate">{camera.location}</p>
            )}
          </div>
        </div>
        {camera.cameraType !== 'General' && (
          <div className="mt-1">
            <Badge variant="default" size="sm">
              {cameraTypeLabels[camera.cameraType]}
            </Badge>
          </div>
        )}
      </div>
    </div>
  );
}
