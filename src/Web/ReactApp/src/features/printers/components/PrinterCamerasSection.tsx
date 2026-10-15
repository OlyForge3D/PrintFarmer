import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Alert, Badge, Button, FormField, Select, Spinner } from '@/common/components/ui';
import type { BadgeVariant } from '@/common/components/ui';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { queryKeys } from '@/common/hooks/queryKeys';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { EditCameraModal } from '@/features/cameras/components/EditCameraModal';
import { ManualCameraFields } from '@/features/printers/components/ManualCameraEntries';
import {
  createEmptyManualCameraDraft,
  toCreateCameraRequest,
  validateManualCameraDraft,
  type ManualCameraDraft,
  type ManualCameraDraftErrors,
} from '@/features/printers/utils/manualCameraDraft';
import { cameraService } from '@/services/cameraService';
import { printerService } from '@/services/printerService';
import { CameraHealthStatus, type CameraDto } from '@/types/api';

interface PrinterCamerasSectionProps {
  printerId: string;
  /** Notifies the parent when a nested dialog opens/closes so it can ignore its own Escape handling. */
  onDialogOpenChange?: (open: boolean) => void;
}

const healthBadgeVariant: Record<CameraHealthStatus, BadgeVariant> = {
  [CameraHealthStatus.Healthy]: 'success',
  [CameraHealthStatus.Degraded]: 'warning',
  [CameraHealthStatus.Unhealthy]: 'error',
  [CameraHealthStatus.Unknown]: 'default',
};

const printerCamerasKey = (printerId: string) => ['cameras', 'list', { printerId }] as const;

function errorMessage(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

/**
 * Lists and manages the cameras linked to a printer (Edit Printer modal).
 * Cameras linked to a single printer inherit its location, so no location is edited here.
 */
export function PrinterCamerasSection({ printerId, onDialogOpenChange }: PrinterCamerasSectionProps) {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canManagePrinterCameras = hasPermission('printers', 'admin');
  const canManageCameras = hasPermission('cameras', 'admin');

  const [editingCamera, setEditingCamera] = useState<CameraDto | null>(null);
  const [removingCamera, setRemovingCamera] = useState<CameraDto | null>(null);
  const [panel, setPanel] = useState<'none' | 'add' | 'link'>('none');
  const [draft, setDraft] = useState<ManualCameraDraft>(() => createEmptyManualCameraDraft());
  const [draftErrors, setDraftErrors] = useState<ManualCameraDraftErrors>({});
  const [linkCameraId, setLinkCameraId] = useState('');

  const dialogOpen = editingCamera !== null || removingCamera !== null;
  useEffect(() => {
    onDialogOpenChange?.(dialogOpen);
  }, [dialogOpen, onDialogOpenChange]);

  const camerasQuery = useQuery({
    queryKey: printerCamerasKey(printerId),
    queryFn: () => cameraService.getAllCameras({ printerId }),
    enabled: !!printerId,
  });
  const cameras = useMemo(() => camerasQuery.data ?? [], [camerasQuery.data]);

  const allCamerasQuery = useQuery({
    queryKey: ['cameras', 'list', 'all'] as const,
    queryFn: () => cameraService.getAllCameras(),
    enabled: panel === 'link',
  });
  const linkableCameras = useMemo(() => {
    const linkedIds = new Set(cameras.map((c) => c.id));
    return (allCamerasQuery.data ?? []).filter((c) => !linkedIds.has(c.id));
  }, [allCamerasQuery.data, cameras]);

  const invalidateCameras = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['cameras'] }),
      queryClient.invalidateQueries({ queryKey: [...queryKeys.printers, 'camera-urls'] }),
    ]);
  };

  const removeMutation = useMutation({
    mutationFn: (camera: CameraDto) => cameraService.removeCameraFromPrinter(printerId, camera.id),
    onSuccess: async (response, camera) => {
      toast.success(
        response.result === 'Deleted'
          ? `Camera "${camera.name}" was deleted.`
          : `Camera "${camera.name}" was unlinked from this printer.`
      );
      setRemovingCamera(null);
      await invalidateCameras();
    },
    onError: (error) => toast.error(errorMessage(error, 'Failed to remove camera.')),
  });

  const discoverMutation = useMutation({
    mutationFn: () => printerService.discoverCameras(printerId),
    onSuccess: async (found) => {
      toast.success(
        found.length === 0
          ? 'No cameras were discovered for this printer.'
          : `Discovered ${found.length} camera${found.length === 1 ? '' : 's'}.`
      );
      await invalidateCameras();
    },
    onError: (error) => toast.error(errorMessage(error, 'Camera discovery failed.')),
  });

  const createMutation = useMutation({
    mutationFn: (value: ManualCameraDraft) => cameraService.createCamera(toCreateCameraRequest(value, printerId)),
    onSuccess: async (camera) => {
      toast.success(`Camera "${camera.name}" added.`);
      setDraft(createEmptyManualCameraDraft());
      setDraftErrors({});
      setPanel('none');
      await invalidateCameras();
    },
    onError: (error) => toast.error(errorMessage(error, 'Failed to add camera.')),
  });

  const linkMutation = useMutation({
    mutationFn: (cameraId: string) => cameraService.linkCameraToPrinter(cameraId, printerId),
    onSuccess: async (camera) => {
      toast.success(`Camera "${camera.name}" linked to this printer.`);
      setLinkCameraId('');
      setPanel('none');
      await invalidateCameras();
    },
    onError: (error) => toast.error(errorMessage(error, 'Failed to link camera.')),
  });

  const submitDraft = () => {
    const errors = validateManualCameraDraft(draft);
    setDraftErrors(errors);
    if (Object.keys(errors).length === 0) {
      createMutation.mutate(draft);
    }
  };

  // The section renders inside the Edit Printer <form>; keep Enter from submitting the printer form.
  const preventFormSubmitOnEnter = (event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Enter' && (event.target as HTMLElement).tagName === 'INPUT') {
      event.preventDefault();
      submitDraft();
    }
  };

  const togglePanel = (next: 'add' | 'link') => {
    setPanel((current) => (current === next ? 'none' : next));
    setDraftErrors({});
  };

  return (
    <div className="space-y-3" data-testid="printer-cameras-section">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-[var(--pf-text-secondary)]">
          Cameras linked to this printer use the printer's location.
        </p>
        <div className="flex flex-wrap gap-2">
          {canManagePrinterCameras && (
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={() => discoverMutation.mutate()}
              disabled={discoverMutation.isPending}
              loading={discoverMutation.isPending}
            >
              Discover cameras
            </Button>
          )}
          {canManageCameras && (
            <>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => togglePanel('add')}
                aria-expanded={panel === 'add'}
                aria-controls="printer-cameras-add-panel"
              >
                Add camera
              </Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => togglePanel('link')}
                aria-expanded={panel === 'link'}
                aria-controls="printer-cameras-link-panel"
              >
                Link existing camera
              </Button>
            </>
          )}
        </div>
      </div>

      {camerasQuery.isLoading ? (
        <div className="flex items-center gap-2 text-sm text-[var(--pf-text-secondary)]" role="status">
          <Spinner size="sm" /> Loading cameras…
        </div>
      ) : camerasQuery.isError ? (
        <Alert variant="error">Failed to load cameras for this printer.</Alert>
      ) : cameras.length === 0 ? (
        <p className="text-sm text-[var(--pf-text-secondary)]">
          No cameras are linked to this printer yet.
        </p>
      ) : (
        <ul className="divide-y divide-[var(--pf-border)] rounded-md border border-[var(--pf-border)]" aria-label="Linked cameras">
          {cameras.map((camera) => {
            const isPrimary = camera.printers?.find((p) => p.id === printerId)?.isPrimary ?? false;
            const otherPrinters = (camera.printers?.length ?? 0) - 1;
            return (
              <li key={camera.id} className="flex flex-wrap items-center justify-between gap-2 p-3">
                <div className="min-w-0 space-y-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium text-[var(--pf-text-primary)]">{camera.name}</span>
                    {isPrimary && <Badge variant="primary">Primary</Badge>}
                    <Badge variant={healthBadgeVariant[camera.healthStatus] ?? 'default'}>
                      {camera.healthStatus}
                    </Badge>
                    {!camera.isEnabled && <Badge variant="warning">Disabled</Badge>}
                  </div>
                  <p className="text-xs text-[var(--pf-text-secondary)]">
                    {camera.source} · {camera.cameraType}
                    {camera.effectiveLocationName
                      ? ` · ${camera.effectiveLocationName}${camera.isLocationDerived ? ' (from printer)' : ''}`
                      : ''}
                    {otherPrinters > 0 ? ` · Shared with ${otherPrinters} other printer${otherPrinters === 1 ? '' : 's'}` : ''}
                  </p>
                </div>
                <div className="flex gap-2">
                  {canManageCameras && (
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      onClick={() => setEditingCamera(camera)}
                      aria-label={`Edit camera ${camera.name}`}
                    >
                      Edit
                    </Button>
                  )}
                  {canManagePrinterCameras && (
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      onClick={() => setRemovingCamera(camera)}
                      aria-label={`Remove camera ${camera.name}`}
                    >
                      Remove
                    </Button>
                  )}
                </div>
              </li>
            );
          })}
        </ul>
      )}

      {panel === 'add' && canManageCameras && (
        <div
          id="printer-cameras-add-panel"
          role="group"
          aria-label="Add camera manually"
          className="space-y-3 rounded-md border border-[var(--pf-border)] p-3"
          onKeyDown={preventFormSubmitOnEnter}
        >
          <ManualCameraFields
            draft={draft}
            errors={draftErrors}
            idPrefix="printer-camera-new"
            disabled={createMutation.isPending}
            onChange={setDraft}
          />
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" size="sm" onClick={() => setPanel('none')}>
              Cancel
            </Button>
            <Button
              type="button"
              variant="primary"
              size="sm"
              onClick={submitDraft}
              disabled={createMutation.isPending}
              loading={createMutation.isPending}
            >
              Save camera
            </Button>
          </div>
        </div>
      )}

      {panel === 'link' && canManageCameras && (
        <div
          id="printer-cameras-link-panel"
          role="group"
          aria-label="Link existing camera"
          className="space-y-3 rounded-md border border-[var(--pf-border)] p-3"
        >
          {allCamerasQuery.isLoading ? (
            <div className="flex items-center gap-2 text-sm" role="status">
              <Spinner size="sm" /> Loading cameras…
            </div>
          ) : linkableCameras.length === 0 ? (
            <p className="text-sm text-[var(--pf-text-secondary)]">No other cameras are available to link.</p>
          ) : (
            <>
              <FormField label="Camera" htmlFor="printer-camera-link-select">
                <Select
                  id="printer-camera-link-select"
                  value={linkCameraId}
                  onChange={(e) => setLinkCameraId(e.target.value)}
                >
                  <option value="">Select a camera…</option>
                  {linkableCameras.map((camera) => (
                    <option key={camera.id} value={camera.id}>{camera.name}</option>
                  ))}
                </Select>
              </FormField>
              <div className="flex justify-end gap-2">
                <Button type="button" variant="ghost" size="sm" onClick={() => setPanel('none')}>
                  Cancel
                </Button>
                <Button
                  type="button"
                  variant="primary"
                  size="sm"
                  onClick={() => linkCameraId && linkMutation.mutate(linkCameraId)}
                  disabled={!linkCameraId || linkMutation.isPending}
                  loading={linkMutation.isPending}
                >
                  Link camera
                </Button>
              </div>
            </>
          )}
        </div>
      )}

      {editingCamera && (
        <EditCameraModal
          camera={editingCamera}
          isOpen
          lockPrinterId={printerId}
          onClose={() => setEditingCamera(null)}
          onSuccess={() => {
            setEditingCamera(null);
            void invalidateCameras();
          }}
        />
      )}

      <ConfirmationModal
        isOpen={removingCamera !== null}
        title="Remove camera"
        message={
          removingCamera
            ? `Remove "${removingCamera.name}" from this printer? Cameras discovered automatically are deleted when no other printer uses them; otherwise the camera is only unlinked.`
            : ''
        }
        confirmButtonText="Remove"
        isDangerous
        isConfirming={removeMutation.isPending}
        onConfirm={() => removingCamera && removeMutation.mutate(removingCamera)}
        onCancel={() => setRemovingCamera(null)}
      />
    </div>
  );
}
