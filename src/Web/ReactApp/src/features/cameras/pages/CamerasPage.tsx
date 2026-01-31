import React, { useState, useEffect } from 'react';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui';
import { CameraIcon, ImageIcon, VideoIcon, SettingsIcon } from '@/common/components/icons/MdiIcons';
import { cameraService } from '@/services/cameraService';
import type { CameraDto } from '@/types/api';
import { Link } from 'react-router';

/**
 * CamerasPage - Display all enabled cameras in a grid view
 * 
 * Shows cameras that have been explicitly added to the system via the Camera Admin page.
 * Supports both stream and snapshot modes for each camera.
 */
export function CamerasPage() {
  const [cameras, setCameras] = useState<CameraDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    loadCameras();
  }, []);

  const loadCameras = async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await cameraService.getEnabledCameras();
      // Sort by sortOrder
      data.sort((a, b) => a.sortOrder - b.sortOrder);
      setCameras(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load cameras');
    } finally {
      setLoading(false);
    }
  };

  return (
    <PageTemplate
      title="Cameras"
      subtitle="Live camera feeds from your print farm"
      icon={CameraIcon}
      actions={
        <Link to="/admin/cameras">
          <Button variant="secondary" iconLeft={<SettingsIcon className="w-4 h-4" />}>
            Manage Cameras
          </Button>
        </Link>
      }
    >
      <div className="space-y-6">
        {/* Error message */}
        {error && (
          <div className="px-4 py-3 rounded bg-pf-error-bg border border-pf-error text-pf-error">
            {error}
          </div>
        )}

        {/* Loading state */}
        {loading ? (
          <div className="text-center py-12">
            <div className="animate-pulse text-pf-text-secondary">Loading cameras...</div>
          </div>
        ) : cameras.length === 0 ? (
          <div className="text-center py-12">
            <CameraIcon className="h-16 w-16 text-pf-text-tertiary mx-auto mb-4" />
            <h3 className="text-xl font-semibold text-pf-text-primary mb-2">No Cameras Configured</h3>
            <p className="text-pf-text-secondary mb-6 max-w-md mx-auto">
              Add cameras to monitor your print farm. You can add standalone webcams or import cameras from your printers.
            </p>
            <Link to="/admin/cameras">
              <Button iconLeft={<SettingsIcon className="w-4 h-4" />}>
                Configure Cameras
              </Button>
            </Link>
          </div>
        ) : (
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
            {cameras.map((camera) => (
              <CameraViewCard key={camera.id} camera={camera} />
            ))}
          </div>
        )}
      </div>
    </PageTemplate>
  );
}

interface CameraViewCardProps {
  camera: CameraDto;
}

/**
 * CameraViewCard - Individual camera feed card
 */
function CameraViewCard({ camera }: CameraViewCardProps) {
  const [imageError, setImageError] = useState(false);
  const [cameraMode, setCameraMode] = useState<'snapshot' | 'stream'>('stream');

  const hasSnapshot = !!camera.snapshotUrl;
  const hasStream = !!camera.streamUrl;

  // Determine which URL to show
  const activeUrl = cameraMode === 'stream' && hasStream
    ? camera.streamUrl
    : cameraMode === 'snapshot' && hasSnapshot
    ? camera.snapshotUrl
    : hasStream
    ? camera.streamUrl
    : camera.snapshotUrl;

  return (
    <div className="rounded-xl shadow-lg backdrop-blur-xl bg-white/5 border border-white/10 hover:border-white/20 transition-colors overflow-hidden flex flex-col">
      {/* Camera feed */}
      <div className="relative w-full aspect-video bg-pf-bg-2 flex items-center justify-center">
        {activeUrl && !imageError ? (
          <img
            src={activeUrl}
            alt={`${camera.name} camera feed`}
            className="w-full h-full object-contain"
            loading="lazy"
            onError={() => setImageError(true)}
          />
        ) : (
          <div className="flex flex-col items-center justify-center text-pf-text-tertiary p-4">
            <CameraIcon className="w-12 h-12 mb-2 opacity-30" />
            <span className="text-sm">{imageError ? 'Camera unavailable' : 'No feed URL'}</span>
          </div>
        )}

        {/* Camera mode toggle - bottom right (only if both modes available) */}
        {hasSnapshot && hasStream && (
          <div className="absolute bottom-2 right-2 flex gap-1 bg-black/50 backdrop-blur-sm rounded p-1">
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
        <div className="flex items-center gap-2">
          <CameraIcon className="w-4 h-4 text-pf-text-tertiary flex-shrink-0" />
          <div className="min-w-0 flex-1">
            <h3 className="font-medium text-pf-text-primary truncate">{camera.name}</h3>
            {camera.location && (
              <p className="text-xs text-pf-text-tertiary truncate">{camera.location}</p>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
