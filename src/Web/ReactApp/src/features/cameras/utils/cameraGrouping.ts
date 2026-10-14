import type { DisplayCameraDto } from '@/types/api';

export type CameraView = 'all' | 'location' | 'printer';

export const CAMERA_VIEWS: readonly CameraView[] = ['all', 'location', 'printer'];

export const UNASSIGNED_LOCATION_GROUP_ID = 'unassigned-location';
export const UNLINKED_PRINTER_GROUP_ID = 'unlinked-printer';

export interface CameraGroup {
  id: string;
  label: string;
  cameras: DisplayCameraDto[];
}

/** Parses the `?view=` search param, defaulting to `all` for missing or unknown values. */
export function parseCameraView(value: string | null | undefined): CameraView {
  return CAMERA_VIEWS.includes(value as CameraView) ? (value as CameraView) : 'all';
}

const byLabel = (a: CameraGroup, b: CameraGroup) =>
  a.label.localeCompare(b.label, undefined, { sensitivity: 'base' });

/** Groups cameras by their effective location; cameras without one land in a trailing "Unassigned" group. */
export function groupCamerasByLocation(cameras: readonly DisplayCameraDto[]): CameraGroup[] {
  const groups = new Map<string, CameraGroup>();
  const unassigned: DisplayCameraDto[] = [];

  for (const camera of cameras) {
    const locationId = camera.effectiveLocationId;
    if (!locationId) {
      unassigned.push(camera);
      continue;
    }
    const group = groups.get(locationId) ?? {
      id: `location-${locationId}`,
      label: camera.effectiveLocationName || 'Unnamed location',
      cameras: [],
    };
    group.cameras.push(camera);
    groups.set(locationId, group);
  }

  const result = [...groups.values()].sort(byLabel);
  if (unassigned.length > 0) {
    result.push({ id: UNASSIGNED_LOCATION_GROUP_ID, label: 'Unassigned', cameras: unassigned });
  }
  return result;
}

/**
 * Groups cameras by linked printer. A camera linked to several printers appears under each of them;
 * cameras without printer links land in a trailing "Not linked to a printer" group.
 */
export function groupCamerasByPrinter(cameras: readonly DisplayCameraDto[]): CameraGroup[] {
  const groups = new Map<string, CameraGroup>();
  const unlinked: DisplayCameraDto[] = [];

  for (const camera of cameras) {
    const printers = camera.printers ?? [];
    if (printers.length === 0) {
      unlinked.push(camera);
      continue;
    }
    for (const printer of printers) {
      const group = groups.get(printer.id) ?? {
        id: `printer-${printer.id}`,
        label: printer.name || 'Unnamed printer',
        cameras: [],
      };
      if (!group.cameras.includes(camera)) {
        group.cameras.push(camera);
      }
      groups.set(printer.id, group);
    }
  }

  const result = [...groups.values()].sort(byLabel);
  if (unlinked.length > 0) {
    result.push({ id: UNLINKED_PRINTER_GROUP_ID, label: 'Not linked to a printer', cameras: unlinked });
  }
  return result;
}
