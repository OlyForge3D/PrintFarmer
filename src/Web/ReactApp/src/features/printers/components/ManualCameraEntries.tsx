import { Button, FormField, Input, Select } from '@/common/components/ui';
import {
  MANUAL_CAMERA_TYPE_OPTIONS,
  type ManualCameraDraft,
  type ManualCameraDraftErrors,
} from '@/features/printers/utils/manualCameraDraft';
import type { CameraType } from '@/types/api';

interface ManualCameraFieldsProps {
  draft: ManualCameraDraft;
  errors?: ManualCameraDraftErrors;
  idPrefix: string;
  disabled?: boolean;
  onChange: (draft: ManualCameraDraft) => void;
}

/**
 * Fields for one manually-entered camera. Intentionally has no location field:
 * a camera linked to a single printer inherits the printer's location.
 */
export function ManualCameraFields({ draft, errors, idPrefix, disabled, onChange }: ManualCameraFieldsProps) {
  const update = (patch: Partial<ManualCameraDraft>) => onChange({ ...draft, ...patch });

  return (
    <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
      <FormField label="Camera name" htmlFor={`${idPrefix}-name`} required error={errors?.name} errorId={`${idPrefix}-name-error`}>
        <Input
          id={`${idPrefix}-name`}
          value={draft.name}
          onChange={(e) => update({ name: e.target.value })}
          placeholder="e.g., Bed camera"
          disabled={disabled}
          aria-invalid={errors?.name ? true : undefined}
          aria-describedby={errors?.name ? `${idPrefix}-name-error` : undefined}
        />
      </FormField>
      <FormField label="Camera type" htmlFor={`${idPrefix}-type`}>
        <Select
          id={`${idPrefix}-type`}
          value={draft.cameraType}
          onChange={(e) => update({ cameraType: e.target.value as CameraType })}
          disabled={disabled}
        >
          {MANUAL_CAMERA_TYPE_OPTIONS.map((type) => (
            <option key={type} value={type}>{type}</option>
          ))}
        </Select>
      </FormField>
      <FormField label="Stream URL" htmlFor={`${idPrefix}-stream`} error={errors?.streamUrl} errorId={`${idPrefix}-stream-error`}>
        <Input
          id={`${idPrefix}-stream`}
          type="url"
          value={draft.streamUrl}
          onChange={(e) => update({ streamUrl: e.target.value })}
          placeholder="http://192.168.1.50/webcam/?action=stream"
          disabled={disabled}
          aria-invalid={errors?.streamUrl ? true : undefined}
          aria-describedby={errors?.streamUrl ? `${idPrefix}-stream-error` : undefined}
        />
      </FormField>
      <FormField label="Snapshot URL" htmlFor={`${idPrefix}-snapshot`} error={errors?.snapshotUrl} errorId={`${idPrefix}-snapshot-error`}>
        <Input
          id={`${idPrefix}-snapshot`}
          type="url"
          value={draft.snapshotUrl}
          onChange={(e) => update({ snapshotUrl: e.target.value })}
          placeholder="http://192.168.1.50/webcam/?action=snapshot"
          disabled={disabled}
          aria-invalid={errors?.snapshotUrl ? true : undefined}
          aria-describedby={errors?.snapshotUrl ? `${idPrefix}-snapshot-error` : undefined}
        />
      </FormField>
    </div>
  );
}

interface ManualCameraEntriesProps {
  drafts: ManualCameraDraft[];
  errors?: Record<string, ManualCameraDraftErrors>;
  disabled?: boolean;
  onChange: (drafts: ManualCameraDraft[]) => void;
  onAdd: () => void;
}

/** Controlled list of manual camera entries used by the Add Printer modal. */
export function ManualCameraEntries({ drafts, errors, disabled, onChange, onAdd }: ManualCameraEntriesProps) {
  return (
    <div className="space-y-3">
      {drafts.map((draft, index) => {
        const label = draft.name.trim() || `camera ${index + 1}`;
        return (
          <div
            key={draft.key}
            role="group"
            aria-labelledby={`manual-camera-${draft.key}-heading`}
            className="space-y-3 rounded-md border border-[var(--pf-border)] p-3"
          >
            <div className="flex items-center justify-between gap-2">
              <h4 id={`manual-camera-${draft.key}-heading`} className="text-sm font-medium text-[var(--pf-text-primary)]">
                Manual camera {index + 1}
              </h4>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => onChange(drafts.filter((d) => d.key !== draft.key))}
                disabled={disabled}
                aria-label={`Remove ${label}`}
              >
                Remove
              </Button>
            </div>
            <ManualCameraFields
              draft={draft}
              errors={errors?.[draft.key]}
              idPrefix={`manual-camera-${draft.key}`}
              disabled={disabled}
              onChange={(next) => onChange(drafts.map((d) => (d.key === draft.key ? next : d)))}
            />
          </div>
        );
      })}
      <Button type="button" variant="secondary" size="sm" onClick={onAdd} disabled={disabled}>
        Add camera manually
      </Button>
    </div>
  );
}
