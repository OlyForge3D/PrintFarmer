import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { RotateCw } from 'lucide-react';
import { toast } from 'sonner';
import { PageTemplate } from '@/common/components/PageTemplate';
import type { EmbeddablePageProps } from '@/common/components/EmbeddablePageProps';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { Button, Toolbar, HeaderActions, RefreshButton, ViewToggle, type ViewModeOption } from '@/common/components/ui';
import { CameraIcon, DeleteIcon, EditIcon, ExternalLinkIcon, SettingsIcon } from '@/common/components/icons/MdiIcons';
import { cameraService } from '@/services/cameraService';
import type { DisplayCameraDto } from '@/types/api';
import { useSearchParams } from 'react-router';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { CameraManagementPanel } from '@/features/cameras/components/CameraManagementPanel';
import { CameraModeControl } from '@/features/cameras/components/CameraModeControl';
import { CameraHealthBadge } from '@/features/cameras/components/CameraHealthBadge';
import { EditCameraModal } from '@/features/cameras/components/EditCameraModal';
import {
  getCameraMediaTransformClassName,
  useCameraViewPreferences,
} from '@/features/cameras/hooks/useCameraViewPreferences';
import { usePrinterSnapshotPreview } from '@/features/cameras/hooks/usePrinterSnapshotPreview';
import { useAuthenticatedMjpegStream } from '@/features/cameras/hooks/useAuthenticatedMjpegStream';
import { getAuthenticatedCameraProxyRoute } from '@/common/auth/authenticatedCameraRoutes';
import { getCameraAttentionContent } from '@/features/cameras/utils/cameraAttention';
import {
  canUseMjpegStream,
  isUnsupportedCameraPreview,
  shouldPollPrinterSnapshot,
} from '@/features/cameras/utils/cameraPreview';
import {
  groupCamerasByLocation,
  groupCamerasByPrinter,
  parseCameraView,
  type CameraGroup,
  type CameraView,
} from '@/features/cameras/utils/cameraGrouping';

const CAMERA_SNAPSHOT_REFRESH_MS = 4_000;

const CAMERA_VIEW_OPTIONS: ViewModeOption<CameraView>[] = [
  { mode: 'all', kind: 'cards', title: 'All cameras' },
  { mode: 'location', kind: 'locationGroups', title: 'By location' },
  { mode: 'printer', kind: 'printerGroups', title: 'By printer' },
];

/**
 * CamerasPage - Top-level `/cameras` page.
 *
 * Shows every configured camera either as a flat grid (`?view=all`, default), grouped by
 * effective location (`?view=location`) or grouped by linked printer (`?view=printer`).
 * Supports both stream and snapshot modes for each camera.
 */
export function CamerasPage({ embedded = false }: EmbeddablePageProps) {
  const auth = useAuth();
  const [error, setError] = useState<string | null>(null);
  const [cameraToEdit, setCameraToEdit] = useState<DisplayCameraDto | null>(null);
  const [cameraToDelete, setCameraToDelete] = useState<DisplayCameraDto | null>(null);
  const [isDeletingCamera, setIsDeletingCamera] = useState(false);

  const [searchParams, setSearchParams] = useSearchParams();
  // Matches the CamerasController manage endpoints' `[RequirePermission("cameras", "admin")]` (#1457).
  const canManageCameras = auth.hasPermission('cameras', 'admin');
  const activeTab = useMemo<'view' | 'manage'>(() => {
    const requestedTab = searchParams.get('cameraTab');
    if (requestedTab === 'manage' && canManageCameras) return 'manage';
    return 'view';
  }, [canManageCameras, searchParams]);
  const activeView = parseCameraView(searchParams.get('view'));

  // Lives under the ['cameras'] key so the `camerasupdated` SignalR invalidation refreshes this page.
  const camerasQuery = useQuery({
    queryKey: ['cameras', 'display'],
    queryFn: async () => {
      const data = await cameraService.getDisplayCameras();
      return [...data].sort((a, b) => a.sortOrder - b.sortOrder);
    },
  });
  const cameras = useMemo(() => camerasQuery.data ?? [], [camerasQuery.data]);
  const cameraGroups = useMemo<CameraGroup[] | null>(() => {
    if (activeView === 'location') return groupCamerasByLocation(cameras);
    if (activeView === 'printer') return groupCamerasByPrinter(cameras);
    return null;
  }, [activeView, cameras]);
  const loading = camerasQuery.isPending;
  const loadError = camerasQuery.error
    ? camerasQuery.error instanceof Error ? camerasQuery.error.message : 'Failed to load cameras'
    : null;
  const displayedError = error ?? loadError;

  const loadCameras = async () => {
    setError(null);
    await camerasQuery.refetch();
  };

  const setTab = (nextTab: 'view' | 'manage') => {
    const nextSearchParams = new URLSearchParams(searchParams);
    nextSearchParams.set('cameraTab', nextTab);
    setSearchParams(nextSearchParams, { replace: true });
  };

  const setView = (nextView: CameraView) => {
    const nextSearchParams = new URLSearchParams(searchParams);
    if (nextView === 'all') {
      nextSearchParams.delete('view');
    } else {
      nextSearchParams.set('view', nextView);
    }
    setSearchParams(nextSearchParams, { replace: true });
  };

  const renderCameraGrid = (gridCameras: DisplayCameraDto[]) => (
    <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
      {gridCameras.map((camera) => (
        <CameraViewCard
          key={camera.id}
          camera={camera}
          canManage={canManageCameras}
          onEdit={setCameraToEdit}
          onDelete={setCameraToDelete}
        />
      ))}
    </div>
  );

  const handleEditSuccess = async () => {
    await loadCameras();
  };

  const handleDeleteCamera = async () => {
    if (!cameraToDelete) return;

    try {
      setIsDeletingCamera(true);
      setError(null);
      await cameraService.deleteCamera(cameraToDelete.id);
      toast.success(`Camera "${cameraToDelete.name}" deleted`);
      setCameraToDelete(null);
      await loadCameras();
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to delete camera';
      setError(message);
      toast.error(`Failed to delete camera: ${message}`);
    } finally {
      setIsDeletingCamera(false);
    }
  };

  return (
    <PageTemplate
      title="Cameras"
      subtitle="Live camera feeds by location, by printer, or all at once"
      icon={CameraIcon}
      actions={
        <HeaderActions>
          {canManageCameras && (
            <Button
              variant="secondary"
              control
              iconLeft={<SettingsIcon className="w-4 h-4" />}
              onClick={() => setTab(activeTab === 'manage' ? 'view' : 'manage')}
            >
              {activeTab === 'manage' ? 'View Cameras' : 'Manage'}
            </Button>
          )}

        </HeaderActions>
      }
      embedded={embedded}
    >
      {activeTab === 'manage' ? (
        <CameraManagementPanel onCamerasChanged={loadCameras} />
      ) : (
        <div className="space-y-6">
          <Toolbar aria-label="Camera controls">
          <ViewToggle
            size="md"
            value={activeView}
            onChange={setView}
            options={CAMERA_VIEW_OPTIONS}
            ariaLabel="Camera view"
          />
          <Toolbar.Spacer />
          <RefreshButton onClick={loadCameras} busy={camerasQuery.isFetching} />
          </Toolbar>

          {/* Error message */}
          {displayedError && (
            <div className="px-4 py-3 rounded-sm bg-pf-error-bg border border-pf-error text-pf-error">
              {displayedError}
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
          ) : cameraGroups ? (
            <div className="space-y-8">
              {cameraGroups.map((group) => {
                const headingId = `camera-group-${group.id}`;
                return (
                  <section key={group.id} aria-labelledby={headingId} className="space-y-3">
                    <h2 id={headingId} className="text-lg font-semibold text-pf-text-primary">
                      {group.label}{' '}
                      <span className="text-sm font-normal text-pf-text-secondary">
                        ({group.cameras.length} {group.cameras.length === 1 ? 'camera' : 'cameras'})
                      </span>
                    </h2>
                    {renderCameraGrid(group.cameras)}
                  </section>
                );
              })}
            </div>
          ) : (
            renderCameraGrid(cameras)
          )}

          <EditCameraModal
            camera={cameraToEdit}
            isOpen={Boolean(cameraToEdit)}
            onClose={() => setCameraToEdit(null)}
            onSuccess={handleEditSuccess}
          />

          <ConfirmationModal
            isOpen={Boolean(cameraToDelete)}
            title="Delete Camera"
            message={cameraToDelete ? `Delete "${cameraToDelete.name}"? This action cannot be undone.` : ''}
            confirmButtonText="Delete"
            isDangerous
            isConfirming={isDeletingCamera}
            onConfirm={handleDeleteCamera}
            onCancel={() => setCameraToDelete(null)}
          />
        </div>
      )}
    </PageTemplate>
  );
}

interface CameraViewCardProps {
  camera: DisplayCameraDto;
  canManage: boolean;
  onEdit: (camera: DisplayCameraDto) => void;
  onDelete: (camera: DisplayCameraDto) => void;
}

/**
 * CameraViewCard - Individual camera feed card
 */
function CameraViewCard({ camera, canManage, onEdit, onDelete }: CameraViewCardProps) {
  const [failedUrl, setFailedUrl] = useState<string | null>(null);

  const previewContract = {
    accessMode: camera.accessMode,
    streamFormat: camera.streamFormat,
    snapshotStrategy: camera.snapshotStrategy,
    streamUrl: camera.streamUrl,
    snapshotUrl: camera.snapshotUrl,
  };
  const pollSnapshotPreview = shouldPollPrinterSnapshot(previewContract);
  const unsupportedPreview = isUnsupportedCameraPreview(previewContract);
  const hasStream = !!camera.streamUrl && canUseMjpegStream(previewContract);
  const directSnapshotUrl = pollSnapshotPreview ? null : camera.snapshotUrl;
  const { previewContainerRef, snapshotSrc, snapshotFailed } = usePrinterSnapshotPreview(
    camera.printerId,
    pollSnapshotPreview,
    CAMERA_SNAPSHOT_REFRESH_MS,
    directSnapshotUrl,
    !!directSnapshotUrl,
    getAuthenticatedCameraProxyRoute(camera.snapshotUrl)
  );
  const snapshotPreviewUrl = snapshotSrc ?? directSnapshotUrl;
  const hasSnapshot = pollSnapshotPreview || !!directSnapshotUrl;
  const {
    cameraMode,
    setCameraMode,
    rotation,
    rotateClockwise,
  } = useCameraViewPreferences({
    preferenceKey: `camera:${camera.id}`,
    defaultMode: hasStream ? 'stream' : 'snapshot',
    hasStream,
    hasSnapshot,
  });

  // Authenticated app-proxy streams are fetched as multipart frames; external streams remain unauthenticated images.
  const { streamSrc, streamUnsupported, streamFailed } = useAuthenticatedMjpegStream(
    camera.streamUrl,
    cameraMode === 'stream' && hasStream,
  );
  const safeStreamRoute = getAuthenticatedCameraProxyRoute(camera.streamUrl);
  const liveStreamSrc = safeStreamRoute ? streamSrc : camera.streamUrl;
  const streamImageFailed = !!liveStreamSrc && failedUrl === liveStreamSrc;
  const streamFallsBackToSnapshot = cameraMode === 'stream' && (streamUnsupported || streamFailed || streamImageFailed) && !!snapshotPreviewUrl;
  const streamIssue = cameraMode === 'stream'
    ? streamUnsupported
      ? 'unsupported'
      : streamFailed || streamImageFailed
        ? 'failed'
        : undefined
    : undefined;
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
  const cameraAttention = getCameraAttentionContent({
    healthStatus: camera.healthStatus,
    healthMessage: camera.healthMessage,
    hasStream,
    hasSnapshot,
    imageError: imageError || snapshotFailed || streamFailed,
    cameraMode,
  });

  return (
    <article
      aria-label={`${camera.name} camera`}
      className="rounded-lg shadow-lg backdrop-blur-xl bg-pf-bg-0/5 border border-white/10 hover:border-white/20 transition-colors overflow-hidden flex flex-col"
    >
      {/* Camera feed */}
      <div ref={previewContainerRef} className="relative w-full aspect-video bg-pf-bg-2">
        {activeUrl && !imageError ? (
          <img
            src={activeUrl}
            alt={showingLiveStream ? `${camera.name} live camera feed` : `${camera.name} camera preview`}
            className={`object-contain bg-black ${mediaClassName}`}
            loading="lazy"
            onError={() => setFailedUrl(activeUrl ?? '')}
          />
        ) : unsupportedPreview ? (
          <div role="status" aria-live="polite" className="absolute inset-0 flex flex-col items-center justify-center text-pf-text-tertiary p-4">
            <CameraIcon className="w-12 h-12 mb-2 opacity-30" />
            <span className="text-center text-sm font-medium text-pf-text-secondary">
              No live preview available
            </span>
            <span className="sr-only">
              This camera does not provide an embeddable MJPEG live stream.
            </span>
          </div>
        ) : (
          <div role="status" aria-live="polite" className="absolute inset-0 flex flex-col items-center justify-center text-pf-text-tertiary p-4">
            <CameraIcon className="w-12 h-12 mb-2 opacity-30" />
            <span className="text-center text-sm font-medium text-pf-text-secondary">
              {cameraAttention?.title
                ?? (streamUnsupported
                  ? hasSnapshot ? 'Live preview unsupported; showing snapshot when available' : 'Live preview unsupported; no snapshot configured'
                  : streamFailed ? 'Live stream unavailable; reconnecting' : cameraMode === 'snapshot' ? 'Snapshot preview unavailable' : 'Connecting to camera')}
            </span>
            {cameraAttention?.issue && (
              <span className="mt-1 max-w-xs text-center text-xs text-pf-text-tertiary">
                {cameraAttention.issue}
              </span>
            )}
            {cameraAttention?.action && (
              <span className="sr-only">
                Action: {cameraAttention.action}
              </span>
            )}
          </div>
        )}
      </div>

      {/* Camera info */}
      <div className="space-y-3 p-3 bg-pf-bg-1">
        <div className="flex items-start gap-2 mb-1">
          <CameraIcon className="w-4 h-4 text-pf-text-tertiary shrink-0 mt-1" />
          <div className="min-w-0 flex-1">
            <h3
              className="font-medium text-pf-text-primary truncate"
              title={[camera.name, camera.printerName, camera.effectiveLocationName ?? camera.location, camera.cameraType, camera.source].filter(Boolean).join(' · ')}
            >{camera.name}</h3>
          </div>
          <CameraHealthBadge
            healthStatus={camera.healthStatus}
            previewFailed={imageError || snapshotFailed || streamUnsupported || streamFailed || streamImageFailed}
            details={cameraAttention ? `${cameraAttention.title}. ${cameraAttention.issue} Action: ${cameraAttention.action}` : undefined}
            size="sm"
          />
          {canManage && (
            <div className="flex shrink-0 items-center gap-1">
              <Button
                type="button"
                variant="subtle"
                size="sm"
                onClick={() => onEdit(camera)}
                className="h-8 w-8 rounded-full p-0"
                title={`Edit ${camera.name}`}
                aria-label={`Edit camera ${camera.name}`}
                iconCenter={<EditIcon className="w-4 h-4" />}
              />
              <Button
                type="button"
                variant="subtle"
                size="sm"
                onClick={() => onDelete(camera)}
                className="h-8 w-8 rounded-full p-0"
                title={`Delete ${camera.name}`}
                aria-label={`Delete camera ${camera.name}`}
                iconCenter={<DeleteIcon className="w-4 h-4 text-pf-error" />}
              />
            </div>
          )}
        </div>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <CameraModeControl
            cameraName={camera.name}
            cameraMode={cameraMode}
            hasStream={hasStream}
            hasSnapshot={hasSnapshot}
            streamUnavailable={!!camera.streamUrl && !hasStream}
            streamIssue={streamIssue}
            onModeChange={setCameraMode}
          />

          <div className="flex flex-wrap items-center gap-2">
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
            {externalUrl && (
              <a
                href={externalUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex h-8 items-center justify-center rounded-sm border border-pf-border bg-pf-bg-2 px-2.5 text-pf-text-primary transition hover:border-pf-border-strong hover:bg-pf-bg-1"
                title={`Open ${camera.name} in a new tab`}
                aria-label={`Open ${camera.name} in a new tab`}
              >
                <ExternalLinkIcon className="w-4 h-4" />
              </a>
            )}
          </div>
        </div>
      </div>
    </article>
  );
}
