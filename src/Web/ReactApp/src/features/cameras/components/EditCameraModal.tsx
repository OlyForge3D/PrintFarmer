import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Alert, Button, Checkbox, FormField, Input, Select, Textarea, Toggle } from '@/common/components/ui';
import { PrinterSearchIcon } from '@/common/components/icons/MdiIcons';
import { usePrinters } from '@/common/hooks/useApi';
import { cameraService } from '@/services/cameraService';
import { locationService } from '@/services/locationService';
import type { CameraConfigDto, DisplayCameraDto, UpdateCameraDto } from '@/types/api';

interface EditCameraModalProps {
  camera: DisplayCameraDto | null;
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void | Promise<void>;
  /** Printer whose link cannot be removed here, e.g. when opened from that printer's Edit Printer dialog. */
  lockPrinterId?: string;
}

interface EditCameraFormData {
  name: string;
  description: string;
  streamUrl: string;
  snapshotUrl: string;
  sortOrder: number;
  isEnabled: boolean;
  /** Linked printers; the primary printer is always first. */
  printerIds: string[];
  /** Explicit location, only used when the camera is not linked to exactly one printer. */
  locationId: string;
}

const emptyFormData: EditCameraFormData = {
  name: '',
  description: '',
  streamUrl: '',
  snapshotUrl: '',
  sortOrder: 0,
  isEnabled: true,
  printerIds: [],
  locationId: '',
};

function getInitialPrinterIds(camera: DisplayCameraDto): string[] {
  const linkedIds = camera.printerIds ?? [];
  if (linkedIds.length > 0) return [...linkedIds];
  return camera.printerId ? [camera.printerId] : [];
}

function getInitialLocationId(camera: DisplayCameraDto): string {
  return camera.locationId ?? camera.effectiveLocationId ?? '';
}

const sameIds = (a: readonly string[], b: readonly string[]) =>
  a.length === b.length && a.every((id, index) => id === b[index]);

export function EditCameraModal({ camera, isOpen, onClose, onSuccess, lockPrinterId }: EditCameraModalProps) {
  const { data: printers = [] } = usePrinters({ enabled: isOpen });
  const { data: locations = [] } = useQuery({
    queryKey: ['locations', 'all'],
    queryFn: () => locationService.getAllLocations(),
    enabled: isOpen,
    staleTime: 300_000,
  });
  const [formData, setFormData] = useState<EditCameraFormData>(emptyFormData);
  const [validationErrors, setValidationErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState(false);
  const [config, setConfig] = useState<CameraConfigDto | null>(null);
  const [configError, setConfigError] = useState(false);

  const detectEndpointsMutation = useMutation({
    mutationFn: (printerId: string) => cameraService.detectCameraEndpoints({ printerId }),
    onSuccess: (result) => {
      setFormData((previous) => ({
        ...previous,
        streamUrl: result.streamUrl ?? previous.streamUrl,
        snapshotUrl: result.snapshotUrl ?? previous.snapshotUrl,
      }));
      setValidationErrors((previous) => {
        const next = { ...previous };
        delete next.streamUrl;
        delete next.snapshotUrl;
        return next;
      });
      toast.success('Camera endpoints detected');
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : 'Failed to detect camera endpoints';
      toast.error(`Failed to detect camera endpoints: ${message}`);
    },
  });

  useEffect(() => {
    setConfig(null);
    setConfigError(false);
    if (!isOpen || !camera) return;
    let cancelled = false;
    setFormData({
      name: camera.name,
      description: camera.description ?? '',
      streamUrl: '',
      snapshotUrl: '',
      sortOrder: camera.sortOrder,
      isEnabled: camera.isEnabled,
      printerIds: getInitialPrinterIds(camera),
      locationId: getInitialLocationId(camera),
    });
    setValidationErrors({});
    setError(null);
    void cameraService.getCameraConfig(camera.id).then((loadedConfig) => {
      if (cancelled) return;
      setConfig(loadedConfig);
      setFormData((previous) => ({
        ...previous,
        streamUrl: loadedConfig.streamUrl ?? '',
        snapshotUrl: loadedConfig.snapshotUrl ?? '',
      }));
    }).catch(() => {
      if (!cancelled) setConfigError(true);
    });
    return () => { cancelled = true; };
  }, [camera, isOpen]);

  const hasChanges = useMemo(() => {
    if (!camera || !config) return false;

    return (
      formData.name !== camera.name ||
      formData.description !== (camera.description ?? '') ||
      formData.streamUrl.trim() !== (config.streamUrl ?? '') ||
      formData.snapshotUrl.trim() !== (config.snapshotUrl ?? '') ||
      formData.sortOrder !== camera.sortOrder ||
      formData.isEnabled !== camera.isEnabled ||
      !sameIds(formData.printerIds, getInitialPrinterIds(camera)) ||
      (formData.printerIds.length !== 1 && formData.locationId !== getInitialLocationId(camera))
    );
  }, [camera, config, formData]);

  const setField = <K extends keyof EditCameraFormData>(field: K, value: EditCameraFormData[K]) => {
    setFormData((previous) => ({ ...previous, [field]: value }));
    setValidationErrors((previous) => {
      if (!previous[field]) return previous;
      const next = { ...previous };
      delete next[field];
      return next;
    });
    setError(null);
  };

  const printerOptions = useMemo(() => {
    const options = printers.map((printer) => ({
      id: printer.id,
      name: printer.name,
      locationName: printer.location?.name ?? null,
    }));
    for (const linked of camera?.printers ?? []) {
      if (!options.some((option) => option.id === linked.id)) {
        options.push({ id: linked.id, name: linked.name, locationName: null });
      }
    }
    return options;
  }, [camera?.printers, printers]);

  const primaryPrinterId = formData.printerIds[0] ?? '';
  const getPrinterName = (printerId: string) =>
    printerOptions.find((option) => option.id === printerId)?.name ?? 'Unknown printer';

  // Location follows the printer when exactly one is linked, or while the server still reports a derived location.
  const derivedLocation = useMemo(() => {
    const unchangedDerived = !!camera?.isLocationDerived && sameIds(getInitialPrinterIds(camera), formData.printerIds);
    if (formData.printerIds.length !== 1 && !unchangedDerived) return null;
    const [printerId] = formData.printerIds;
    const option = printerOptions.find((printerOption) => printerOption.id === printerId);
    return {
      printerName: option?.name ?? 'the linked printer',
      locationName: (unchangedDerived ? camera?.effectiveLocationName : option?.locationName) ?? 'No location assigned',
    };
  }, [camera, formData.printerIds, printerOptions]);

  const togglePrinter = (printerId: string, checked: boolean) => {
    if (!checked && printerId === lockPrinterId) return;
    setField(
      'printerIds',
      checked
        ? [...formData.printerIds.filter((id) => id !== printerId), printerId]
        : formData.printerIds.filter((id) => id !== printerId),
    );
  };

  const setPrimaryPrinter = (printerId: string) => {
    setField('printerIds', [printerId, ...formData.printerIds.filter((id) => id !== printerId)]);
  };

  const validateForm = (): boolean => {
    const errors: Record<string, string> = {};

    if (!formData.name.trim()) {
      errors.name = 'Camera name is required.';
    }

    const streamConfigured = !!formData.streamUrl.trim()
      || (config?.streamUrlHasCredentials && formData.streamUrl === (config.streamUrl ?? ''));
    const snapshotConfigured = !!formData.snapshotUrl.trim()
      || (config?.snapshotUrlHasCredentials && formData.snapshotUrl === (config.snapshotUrl ?? ''));
    if (!streamConfigured && !snapshotConfigured) {
      errors.streamUrl = 'Add a stream URL or snapshot URL.';
      errors.snapshotUrl = 'Add a stream URL or snapshot URL.';
    }

    setValidationErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const handleSubmit = async () => {
    if (!camera || !config || configError || !validateForm()) return;

    const request: UpdateCameraDto = {
      name: formData.name.trim(),
      description: formData.description.trim() || undefined,
      sortOrder: formData.sortOrder,
      isEnabled: formData.isEnabled,
    };
    if (!sameIds(formData.printerIds, getInitialPrinterIds(camera))) {
      request.printerIds = formData.printerIds;
    }
    // Unchanged links are omitted so the backend keeps every linked printer and the primary ordering.
    // A camera linked to exactly one printer always follows that printer's location.
    if (!derivedLocation) {
      if (formData.locationId) {
        request.locationId = formData.locationId;
      } else if (camera.locationId || camera.effectiveLocationId) {
        request.clearLocationId = true;
      }
    }
    if (formData.streamUrl.trim() !== (config.streamUrl ?? '')) {
      request.streamUrl = formData.streamUrl.trim();
    }
    if (formData.snapshotUrl.trim() !== (config.snapshotUrl ?? '')) {
      request.snapshotUrl = formData.snapshotUrl.trim();
    }

    try {
      setIsSaving(true);
      setError(null);
      await cameraService.updateCamera(camera.id, request);
      await onSuccess();
      toast.success(`Camera "${request.name}" updated`);
      onClose();
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to update camera';
      setError(message);
      toast.error(`Failed to update camera: ${message}`);
    } finally {
      setIsSaving(false);
    }
  };

  const handleDetectEndpoints = () => {
    if (!primaryPrinterId) {
      toast.error('Select a printer before detecting camera endpoints.');
      return;
    }

    detectEndpointsMutation.mutate(primaryPrinterId);
  };

  const handleClose = () => {
    if (isSaving) return;
    onClose();
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Edit Camera"
      size="lg"
      isDisabled={isSaving}
      footer={
        <>
          <Button type="button" variant="secondary" onClick={handleClose} disabled={isSaving}>
            Cancel
          </Button>
          <Button type="button" onClick={handleSubmit} loading={isSaving} disabled={!config || configError || !hasChanges}>
            Save Camera
          </Button>
        </>
      }
    >
      <fieldset disabled={!config || configError || isSaving} className="space-y-4">
        <legend className="sr-only">Camera configuration</legend>
        {configError ? (
          <Alert type="error" title="Could not load camera configuration">
            Close and reopen this dialog to retry. No camera changes can be saved until configuration loads.
          </Alert>
        ) : !config && (
          <p role="status">Loading camera configuration…</p>
        )}
        {error && (
          <Alert type="error" title="Could not save camera">
            {error}
          </Alert>
        )}

        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <FormField label="Camera Name" htmlFor="edit-camera-name" required error={validationErrors.name}>
            <Input
              id="edit-camera-name"
              value={formData.name}
              onChange={(event) => setField('name', event.target.value)}
              invalid={Boolean(validationErrors.name)}
              aria-required="true"
              placeholder="Workshop Camera 1"
            />
          </FormField>

          {derivedLocation ? (
            <FormField
              label="Location"
              htmlFor="edit-camera-location"
              helper={`Location follows ${derivedLocation.printerName}`}
            >
              <Input id="edit-camera-location" value={derivedLocation.locationName} disabled readOnly />
            </FormField>
          ) : (
            <FormField
              label="Location"
              htmlFor="edit-camera-location"
              helper="Where this camera is installed. Cameras linked to a single printer follow that printer's location."
            >
              <Select
                id="edit-camera-location"
                value={formData.locationId}
                onChange={(event) => setField('locationId', event.target.value)}
              >
                <option value="">No location</option>
                {locations.map((location) => (
                  <option key={location.id} value={location.id}>
                    {location.path || location.name}
                  </option>
                ))}
                {formData.locationId && !locations.some((location) => location.id === formData.locationId) && (
                  <option value={formData.locationId}>{camera?.effectiveLocationName ?? 'Current location'}</option>
                )}
              </Select>
            </FormField>
          )}
        </div>

        <fieldset className="space-y-3 rounded-sm border border-pf-border p-3" aria-describedby="edit-camera-printers-help">
          <legend className="px-1 text-sm font-medium text-pf-text-primary">Linked Printers</legend>
          <p id="edit-camera-printers-help" className="text-sm text-pf-text-secondary">
            Link this camera to any number of printers. With no printers or several printers, choose the location below.
          </p>
          {lockPrinterId && formData.printerIds.includes(lockPrinterId) ? (
            <p id="edit-camera-locked-printer-help" className="text-sm text-pf-text-tertiary">
              {getPrinterName(lockPrinterId)} stays linked while editing from that printer; remove the camera from its camera list instead.
            </p>
          ) : null}
          {printerOptions.length === 0 ? (
            <p className="text-sm text-pf-text-tertiary">No printers available.</p>
          ) : (
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
              {printerOptions.map((printer) => (
                <Checkbox
                  key={printer.id}
                  id={`edit-camera-printer-${printer.id}`}
                  label={printer.name}
                  checked={formData.printerIds.includes(printer.id)}
                  disabled={printer.id === lockPrinterId && formData.printerIds.includes(printer.id)}
                  aria-describedby={printer.id === lockPrinterId ? 'edit-camera-locked-printer-help' : undefined}
                  onChange={(event) => togglePrinter(printer.id, event.target.checked)}
                />
              ))}
            </div>
          )}

          <div className="grid grid-cols-1 gap-4 md:grid-cols-[1fr_auto] md:items-end">
            {formData.printerIds.length > 1 ? (
              <FormField
                label="Primary Printer"
                htmlFor="edit-camera-primary-printer"
                helper="Used for endpoint detection and as the camera's representative printer."
              >
                <Select
                  id="edit-camera-primary-printer"
                  value={primaryPrinterId}
                  onChange={(event) => setPrimaryPrinter(event.target.value)}
                >
                  {formData.printerIds.map((printerId) => (
                    <option key={printerId} value={printerId}>
                      {getPrinterName(printerId)}
                    </option>
                  ))}
                </Select>
              </FormField>
            ) : (
              <p className="text-sm text-pf-text-secondary">
                {primaryPrinterId ? `Linked to ${getPrinterName(primaryPrinterId)}` : 'Not linked to a printer'}
              </p>
            )}

            <Button
              type="button"
              variant="secondary"
              onClick={handleDetectEndpoints}
              loading={detectEndpointsMutation.isPending}
              disabled={!primaryPrinterId || isSaving}
              iconLeft={<PrinterSearchIcon className="w-4 h-4" />}
              className="md:mb-0"
            >
              Detect Endpoints
            </Button>
          </div>
        </fieldset>

        <FormField
          label="Stream URL"
          htmlFor="edit-camera-stream-url"
          helper={config?.streamUrlHasCredentials
            ? 'Stored credentials are hidden. Leave this URL unchanged to preserve them; replacing it overwrites the saved URL and credentials.'
            : 'MJPEG stream URL. Use a transcoder for RTSP cameras.'}
          error={validationErrors.streamUrl}
        >
          <Input
            id="edit-camera-stream-url"
            type="url"
            value={formData.streamUrl}
            onChange={(event) => setField('streamUrl', event.target.value)}
            invalid={Boolean(validationErrors.streamUrl)}
            placeholder="http://192.168.1.100:8080/stream"
          />
        </FormField>

        <FormField
          label="Snapshot URL"
          htmlFor="edit-camera-snapshot-url"
          error={validationErrors.snapshotUrl}
          helper={config?.snapshotUrlHasCredentials
            ? 'Stored credentials are hidden. Leave this URL unchanged to preserve them; replacing it overwrites the saved URL and credentials.'
            : undefined}
        >
          <Input
            id="edit-camera-snapshot-url"
            type="url"
            value={formData.snapshotUrl}
            onChange={(event) => setField('snapshotUrl', event.target.value)}
            invalid={Boolean(validationErrors.snapshotUrl)}
            placeholder="http://192.168.1.100:8080/snapshot"
          />
        </FormField>

        <FormField label="Description" htmlFor="edit-camera-description">
          <Textarea
            id="edit-camera-description"
            value={formData.description}
            onChange={(event) => setField('description', event.target.value)}
            placeholder="Optional description"
            rows={3}
          />
        </FormField>

        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <FormField
            label="Sort Order"
            htmlFor="edit-camera-sort-order"
            helper="Lower numbers appear first."
          >
            <Input
              id="edit-camera-sort-order"
              type="number"
              value={formData.sortOrder}
              onChange={(event) => setField('sortOrder', Number.parseInt(event.target.value, 10) || 0)}
              className="md:max-w-32"
            />
          </FormField>

          <FormField label="Camera Status" htmlFor="edit-camera-enabled">
            <Toggle
              id="edit-camera-enabled"
              checked={formData.isEnabled}
              onChange={(event) => setField('isEnabled', event.target.checked)}
              label={formData.isEnabled ? 'Enabled' : 'Disabled'}
            />
          </FormField>
        </div>
      </fieldset>
    </Modal>
  );
}
