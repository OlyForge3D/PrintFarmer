import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { PrinterFileCatalogEntry, PrinterFileCatalogSource, PrinterFilePreview } from '@/types/api';
import { describeReasonCode } from '@/features/files/utils/fileDisplay';

/** Captured previews are bound to an observation revision, so they can be cached for a long time. */
const PREVIEW_STALE_MS = 10 * 60_000;
/** A busy printer answers `Deferred`; re-ask sooner than a settled answer. */
const DEFERRED_STALE_MS = 60_000;

export type PrinterFilePreviewTarget = Pick<PrinterFileCatalogEntry, 'id' | 'observationRevision'>;

export interface PrinterFilePreviewResult {
  /** Server answer, when fetched. */
  preview: PrinterFilePreview | undefined;
  /** Captured image bytes, when the server has an image for this exact observation. */
  thumbnail: Blob | null;
  isLoading: boolean;
  /** Set once the server (or the source capabilities) said no image can be shown. */
  unavailableReason: string | null;
}

/** Preview is offered only when the backend supports it AND the caller may read it. */
export function canPreviewSource(source: PrinterFileCatalogSource | undefined): boolean {
  return source?.supportsPreview === true && source.canPreview === true;
}

/**
 * Prefers the explicit download capability; older catalog payloads without it fall back to
 * the copy capability (the server derives canDownload from supportsDownload && copy authority).
 */
export function canDownloadSource(source: PrinterFileCatalogSource | undefined): boolean {
  if (!source) return false;
  if (source.supportsDownload !== undefined || source.canDownload !== undefined) {
    return source.supportsDownload === true && source.canDownload === true;
  }
  return source.canCopy && source.supportsCopy;
}

export function unsupportedPreviewReason(source: PrinterFileCatalogSource | undefined): string {
  if (source?.supportsPreview !== true) return 'not supported for this printer';
  return 'you do not have permission to view previews';
}

function errorReason(error: unknown): string {
  const data = (error as { data?: { reasonCode?: unknown; code?: unknown } } | null)?.data;
  const code = typeof data?.reasonCode === 'string' ? data.reasonCode
    : typeof data?.code === 'string' ? data.code : null;
  return describeReasonCode(code) ?? 'preview request failed';
}

/**
 * Lazily loads the captured preview + thumbnail for one printer-resident observation.
 * Pass `enabled=false` until the item is on screen (or open in the detail panel) and the
 * source allows previews; nothing is requested otherwise. Results are cached per
 * location + observation revision.
 */
export function usePrinterFilePreview(target: PrinterFilePreviewTarget, enabled: boolean): PrinterFilePreviewResult {
  const previewQuery = useQuery({
    queryKey: ['printer-file-preview', target.id, target.observationRevision],
    queryFn: ({ signal }) => apiClient.getPrinterFilePreview(target.id, target.observationRevision, signal),
    enabled,
    staleTime: (query) => (query.state.data?.state === 'Deferred' ? DEFERRED_STALE_MS : PREVIEW_STALE_MS),
    gcTime: PREVIEW_STALE_MS,
    retry: false,
  });
  const preview = previewQuery.data;
  const wantsImage = enabled && preview?.state === 'Available' && preview.hasImage;
  const thumbnailQuery = useQuery({
    queryKey: ['printer-file-thumbnail', target.id, target.observationRevision],
    queryFn: ({ signal }) => apiClient.getPrinterFileLocationThumbnail(target.id, target.observationRevision, signal),
    enabled: wantsImage,
    staleTime: PREVIEW_STALE_MS,
    gcTime: PREVIEW_STALE_MS,
    retry: false,
  });

  let unavailableReason: string | null = null;
  if (previewQuery.error) {
    unavailableReason = errorReason(previewQuery.error);
  } else if (preview && preview.state !== 'Available') {
    unavailableReason = describeReasonCode(preview.reasonCode) ?? preview.state.toLowerCase();
  } else if (preview && !preview.hasImage) {
    unavailableReason = describeReasonCode(preview.reasonCode) ?? 'image unavailable';
  } else if (thumbnailQuery.error) {
    unavailableReason = errorReason(thumbnailQuery.error);
  } else if (thumbnailQuery.isSuccess && thumbnailQuery.data === null) {
    unavailableReason = 'image unavailable';
  }

  return {
    preview,
    thumbnail: thumbnailQuery.data ?? null,
    isLoading: enabled && (previewQuery.isLoading || (wantsImage && thumbnailQuery.isLoading)),
    unavailableReason,
  };
}