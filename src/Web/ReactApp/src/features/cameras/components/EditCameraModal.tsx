import { useEffect, useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Alert, Button, FormField, Input, Textarea, Toggle } from '@/common/components/ui';
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
}

const emptyFormData: EditCameraFormData = {
  name: '',
  description: '',
  streamUrl: '',
  snapshotUrl: '',
  location: '',
  sortOrder: 0,
  isEnabled: true,
};

export function EditCameraModal({ camera, isOpen, onClose, onSuccess }: EditCameraModalProps) {
  const [formData, setFormData] = useState<EditCameraFormData>(emptyFormData);
  const [validationErrors, setValidationErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState(false);

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
      formData.isEnabled !== camera.isEnabled
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
