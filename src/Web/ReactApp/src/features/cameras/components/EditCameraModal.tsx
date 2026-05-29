import { useEffect, useMemo, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Alert, Button, FormField, Input, Select, Textarea, Toggle } from '@/common/components/ui';
import { PrinterSearchIcon } from '@/common/components/icons/MdiIcons';
import { usePrinters } from '@/common/hooks/useApi';
import { cameraService } from '@/services/cameraService';
import type { DisplayCameraDto, UpdateCameraDto } from '@/types/api';

interface EditCameraModalProps {
  camera: DisplayCameraDto | null;
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void | Promise<void>;
}

interface EditCameraFormData {
  name: string;
  description: string;
  streamUrl: string;
  snapshotUrl: string;
  location: string;
  sortOrder: number;
  isEnabled: boolean;
  printerId: string;
}

const emptyFormData: EditCameraFormData = {
  name: '',
  description: '',
  streamUrl: '',
  snapshotUrl: '',
  location: '',
  sortOrder: 0,
  isEnabled: true,
  printerId: '',
};

export function EditCameraModal({ camera, isOpen, onClose, onSuccess }: EditCameraModalProps) {
  const { data: printers = [] } = usePrinters({ enabled: isOpen });
  const [formData, setFormData] = useState<EditCameraFormData>(emptyFormData);
  const [validationErrors, setValidationErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState(false);

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
    if (!isOpen || !camera) return;

    setFormData({
      name: camera.name,
      description: camera.description ?? '',
      streamUrl: camera.streamUrl ?? '',
      snapshotUrl: camera.snapshotUrl ?? '',
      location: camera.location ?? '',
      sortOrder: camera.sortOrder,
      isEnabled: camera.isEnabled,
      printerId: camera.printerId ?? '',
    });
    setValidationErrors({});
    setError(null);
  }, [camera, isOpen]);

  const hasChanges = useMemo(() => {
    if (!camera) return false;

    return (
      formData.name !== camera.name ||
      formData.description !== (camera.description ?? '') ||
      formData.streamUrl !== (camera.streamUrl ?? '') ||
      formData.snapshotUrl !== (camera.snapshotUrl ?? '') ||
      formData.location !== (camera.location ?? '') ||
      formData.sortOrder !== camera.sortOrder ||
      formData.isEnabled !== camera.isEnabled ||
      formData.printerId !== (camera.printerId ?? '')
    );
  }, [camera, formData]);

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

  const selectedPrinterName = useMemo(() => {
    if (!formData.printerId) return null;
    return printers.find((printer) => printer.id === formData.printerId)?.name ?? camera?.printerName ?? null;
  }, [camera?.printerName, formData.printerId, printers]);

  const validateForm = (): boolean => {
    const errors: Record<string, string> = {};

    if (!formData.name.trim()) {
      errors.name = 'Camera name is required.';
    }

    if (!formData.streamUrl.trim() && !formData.snapshotUrl.trim()) {
      errors.streamUrl = 'Add a stream URL or snapshot URL.';
      errors.snapshotUrl = 'Add a stream URL or snapshot URL.';
    }

    setValidationErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const handleSubmit = async () => {
    if (!camera || !validateForm()) return;

    const request: UpdateCameraDto = {
      name: formData.name.trim(),
      description: formData.description.trim() || undefined,
      streamUrl: formData.streamUrl.trim() || undefined,
      snapshotUrl: formData.snapshotUrl.trim() || undefined,
      location: formData.location.trim() || undefined,
      sortOrder: formData.sortOrder,
      isEnabled: formData.isEnabled,
      printerId: formData.printerId || null,
    };

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
    if (!formData.printerId) {
      toast.error('Select a printer before detecting camera endpoints.');
      return;
    }

    detectEndpointsMutation.mutate(formData.printerId);
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
          <Button type="button" onClick={handleSubmit} loading={isSaving} disabled={!hasChanges}>
            Save Camera
          </Button>
        </>
      }
    >
      <div className="space-y-4">
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

          <FormField label="Location" htmlFor="edit-camera-location">
            <Input
              id="edit-camera-location"
              value={formData.location}
              onChange={(event) => setField('location', event.target.value)}
              placeholder="Workshop, Main Room"
            />
          </FormField>
        </div>

        <div className="grid grid-cols-1 gap-4 md:grid-cols-[1fr_auto] md:items-end">
          <FormField
            label="Associated Printer"
            htmlFor="edit-camera-printer"
            helper={
              selectedPrinterName
                ? `Linked to ${selectedPrinterName}`
                : 'Optional printer link for endpoint detection and camera grouping.'
            }
          >
            <Select
              id="edit-camera-printer"
              value={formData.printerId}
              onChange={(event) => setField('printerId', event.target.value)}
              label="Associated Printer"
            >
              <option value="">No printer linked</option>
              {printers.map((printer) => (
                <option key={printer.id} value={printer.id}>
                  {printer.name}
                </option>
              ))}
              {camera?.printerId && !printers.some((printer) => printer.id === camera.printerId) && (
                <option value={camera.printerId}>{camera.printerName ?? 'Current printer'}</option>
              )}
            </Select>
          </FormField>

          <Button
            type="button"
            variant="secondary"
            onClick={handleDetectEndpoints}
            loading={detectEndpointsMutation.isPending}
            disabled={!formData.printerId || isSaving}
            iconLeft={<PrinterSearchIcon className="w-4 h-4" />}
            className="md:mb-0"
          >
            Detect Endpoints
          </Button>
        </div>

        <FormField
          label="Stream URL"
          htmlFor="edit-camera-stream-url"
          helper="MJPEG or HLS stream URL. Use a transcoder for RTSP cameras."
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

        <FormField label="Snapshot URL" htmlFor="edit-camera-snapshot-url" error={validationErrors.snapshotUrl}>
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
      </div>
    </Modal>
  );
}
