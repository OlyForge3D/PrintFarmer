import { isSafeHttpUrl } from '@/common/utils/validation';
import { generateUUID } from '@/utils/uuid';
import { CameraSource, CameraType } from '@/types/api';
import type { CreateCameraDto } from '@/types/api';

/** A manually-entered camera that will be linked to a single printer (location derived from the printer). */
export interface ManualCameraDraft {
  key: string;
  name: string;
  streamUrl: string;
  snapshotUrl: string;
  cameraType: CameraType;
}

export type ManualCameraDraftErrors = Partial<Record<'name' | 'streamUrl' | 'snapshotUrl', string>>;

export const MANUAL_CAMERA_TYPE_OPTIONS: readonly CameraType[] = [
  CameraType.General,
  CameraType.Bed,
  CameraType.Nozzle,
  CameraType.Wide,
  CameraType.Timelapse,
];

export function createEmptyManualCameraDraft(): ManualCameraDraft {
  return {
    key: generateUUID(),
    name: '',
    streamUrl: '',
    snapshotUrl: '',
    cameraType: CameraType.General,
  };
}

export function validateManualCameraDraft(draft: ManualCameraDraft): ManualCameraDraftErrors {
  const errors: ManualCameraDraftErrors = {};
  const streamUrl = draft.streamUrl.trim();
  const snapshotUrl = draft.snapshotUrl.trim();

  if (!draft.name.trim()) {
    errors.name = 'Camera name is required.';
  }

  if (!streamUrl && !snapshotUrl) {
    errors.streamUrl = 'Add a stream URL or snapshot URL.';
    errors.snapshotUrl = 'Add a stream URL or snapshot URL.';
  } else {
    if (streamUrl && !isSafeHttpUrl(streamUrl)) {
      errors.streamUrl = 'Enter a valid http:// or https:// stream URL.';
    }
    if (snapshotUrl && !isSafeHttpUrl(snapshotUrl)) {
      errors.snapshotUrl = 'Enter a valid http:// or https:// snapshot URL.';
    }
  }

  return errors;
}

/**
 * Builds the create request for a manual camera linked to exactly one printer.
 * No location is sent: a single-printer camera's location is derived from the printer.
 */
export function toCreateCameraRequest(draft: ManualCameraDraft, printerId: string): CreateCameraDto {
  const streamUrl = draft.streamUrl.trim();
  const snapshotUrl = draft.snapshotUrl.trim();
  return {
    name: draft.name.trim(),
    streamUrl: streamUrl || undefined,
    snapshotUrl: snapshotUrl || undefined,
    cameraType: draft.cameraType,
    source: CameraSource.Standalone,
    isEnabled: true,
    printerIds: [printerId],
  };
}
