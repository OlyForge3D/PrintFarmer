import type { DisplayCameraDto } from '@/types/api';

/** What a camera card has observed in this browser; absent until the card has evidence either way. */
export type CameraPreviewDelivery = 'delivering' | 'failed';

export type CameraPreviewDeliveryById = Readonly<Record<string, CameraPreviewDelivery>>;

/** Printer id → online flag; `null` when printer status is unavailable. */
export type PrinterOnlineById = ReadonlyMap<string, boolean> | null;

const DisplayTier = {
  OnlineDelivering: 0,
  OnlineWithoutImages: 1,
  Offline: 2,
} as const;
type DisplayTier = (typeof DisplayTier)[keyof typeof DisplayTier];

/**
 * A camera is offline only when every linked printer is known to be offline. Standalone cameras
 * and cameras whose printer status is unknown are never demoted on printer status alone.
 */
export function isCameraOffline(camera: DisplayCameraDto, printerOnlineById: PrinterOnlineById): boolean {
  if (!printerOnlineById) return false;
  const printerIds = camera.printerIds?.length ? camera.printerIds : camera.printerId ? [camera.printerId] : [];
  if (printerIds.length === 0) return false;
  return printerIds.every((printerId) => printerOnlineById.get(printerId) === false);
}

function getDisplayTier(
  camera: DisplayCameraDto,
  deliveryById: CameraPreviewDeliveryById,
  printerOnlineById: PrinterOnlineById,
): DisplayTier {
  if (isCameraOffline(camera, printerOnlineById)) return DisplayTier.Offline;
  return deliveryById[camera.id] === 'delivering' ? DisplayTier.OnlineDelivering : DisplayTier.OnlineWithoutImages;
}

/**
 * Orders cameras online-and-delivering first, then online cameras without a delivered image
 * (failed or not yet loaded), then offline cameras. The incoming order is preserved within a tier.
 */
export function orderCamerasForDisplay(
  cameras: readonly DisplayCameraDto[],
  deliveryById: CameraPreviewDeliveryById,
  printerOnlineById: PrinterOnlineById,
): DisplayCameraDto[] {
  return cameras
    .map((camera, index) => ({ camera, index, tier: getDisplayTier(camera, deliveryById, printerOnlineById) }))
    .sort((a, b) => a.tier - b.tier || a.index - b.index)
    .map(({ camera }) => camera);
}
