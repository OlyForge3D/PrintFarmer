import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type {
  PrinterFileCatalogEntry,
  PrinterFileCatalogSource,
  PrinterFileEnrichmentState,
  PrinterFilePreview,
} from '@/types/api';
import { describeReasonCode, isCollectingState } from '@/features/files/utils/fileDisplay';

/** Captured previews are bound to an observation revision, so they can be cached for a long time. */
const PREVIEW_STALE_MS = 10 * 60_000;
/** A busy printer or pending background capture is unsettled; re-ask sooner than a settled answer. */
const UNSETTLED_STALE_MS = 60_000;

/**
 * The catalog row (already polled by the catalog page) optionally carries enrichment status.
 * Those fields are part of the preview's cache identity, so a background status change seen by
 * the single catalog poll re-reads this preview once - no per-item timers.
 */
export type PrinterFilePreviewTarget = Pick<PrinterFileCatalogEntry, 'id' | 'observationRevision'>
  & Partial<Pick<PrinterFileCatalogEntry, 'metadataState' | 'thumbnailState' | 'thumbnailVersion'>>;

export interface PrinterFilePreviewResult {
  /** Server answer, when fetched. */
  preview: PrinterFilePreview | undefined;
  /** Captured image bytes, when the server has an image for this exact observation. */
  thumbnail: Blob | null;
  isLoading: boolean;
  /** Set once the server (or the source capabilities) said no image can be shown. */
  unavailableReason: string | null;
  /** Background thumbnail capture has not run yet: a calm pending state, not an error. */
  thumbnailCollecting: boolean;
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

/** Plain-language fallbacks for settled thumbnail outcomes the server gave no reason code for. */
const SETTLED_THUMBNAIL_REASON: Partial<Record<PrinterFileEnrichmentState, string>> = {
  Absent: 'the file has no embedded preview',
  Unsupported: 'not supported for this file',
  Failed: 'preview capture failed',
};

function noImageReason(preview: PrinterFilePreview): string {
  const coded = describeReasonCode(preview.thumbnailReason) ?? describeReasonCode(preview.reasonCode);
  if (coded) return coded;
  const settled = preview.thumbnailState ? SETTLED_THUMBNAIL_REASON[preview.thumbnailState] : undefined;
  if (settled) return settled;
  return preview.state !== 'Available' ? preview.state.toLowerCase() : 'image unavailable';
}

function isUnsettled(preview: PrinterFilePreview | undefined): boolean {
  return preview?.state === 'Deferred' || preview?.isStale === true
    || isCollectingState(preview?.metadataState) || isCollectingState(preview?.thumbnailState);
}

/**
 * Lazily loads the captured preview + thumbnail for one printer-resident observation.
 * Pass `enabled=false` until the item is on screen (or open in the detail panel) and the
 * source allows previews; nothing is requested otherwise. Both reads are local-only on the
 * server. Results are cached per location + observation revision, and thumbnail bytes also
 * per opaque `thumbnailVersion`, so a newly stored image never reuses an older blob.
 */
export function usePrinterFilePreview(target: PrinterFilePreviewTarget, enabled: boolean): PrinterFilePreviewResult {
  const previewQuery = useQuery({
    queryKey: [
      'printer-file-preview', target.id, target.observationRevision,
      target.metadataState ?? null, target.thumbnailState ?? null, target.thumbnailVersion ?? null,
    ],
    queryFn: ({ signal }) => apiClient.getPrinterFilePreview(target.id, target.observationRevision, signal),
    enabled,
    staleTime: (query) => (isUnsettled(query.state.data) ? UNSETTLED_STALE_MS : PREVIEW_STALE_MS),
    gcTime: PREVIEW_STALE_MS,
    retry: false,
    // While a changed enrichment status is re-read, keep showing this observation's last answer.
    placeholderData: (previous) => previous?.locationId === target.id
      && previous.observationRevision === target.observationRevision ? previous : undefined,
  });
  const preview = previewQuery.data;
  const thumbnailVersion = preview?.thumbnailVersion ?? null;
  // Older servers only had images for `Available`; enrichment-aware servers may also serve a
  // stale-but-valid image while a refresh is pending, which `hasImage` alone reports.
  const wantsImage = enabled && preview?.hasImage === true
    && (preview.state === 'Available' || preview.thumbnailState != null);
  const thumbnailQuery = useQuery({
    queryKey: ['printer-file-thumbnail', target.id, target.observationRevision, thumbnailVersion],
    queryFn: ({ signal }) => apiClient.getPrinterFileLocationThumbnail(
      target.id, target.observationRevision, thumbnailVersion, signal),
    enabled: wantsImage,
    staleTime: PREVIEW_STALE_MS,
    gcTime: PREVIEW_STALE_MS,
    retry: false,
  });

  const thumbnailCollecting = enabled && !previewQuery.error && preview != null && !wantsImage
    && isCollectingState(preview.thumbnailState);
  let unavailableReason: string | null = null;
  if (previewQuery.error) {
    unavailableReason = errorReason(previewQuery.error);
  } else if (preview && !wantsImage && !thumbnailCollecting) {
    unavailableReason = noImageReason(preview);
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
    thumbnailCollecting,
  };
}