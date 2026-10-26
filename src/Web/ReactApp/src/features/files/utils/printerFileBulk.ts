import type { PrinterFileCatalogEntry, PrinterFileCatalogSource } from '@/types/api';
import { canDownloadSource } from '@/features/files/hooks/usePrinterFilePreview';
import { describeReasonCode } from '@/features/files/utils/fileDisplay';

/** Which bulk actions apply to one listed printer file. */
export interface PrinterFileBulkCapabilities {
  copy: boolean;
  download: boolean;
  delete: boolean;
}

const NO_BULK_ACTIONS: PrinterFileBulkCapabilities = { copy: false, download: false, delete: false };

/**
 * Mirrors the per-file actions: every bulk action needs an Available observation plus the
 * source capability. The server still re-checks authority and freshness on every request.
 */
export function printerFileBulkCapabilities(
  entry: PrinterFileCatalogEntry,
  source: PrinterFileCatalogSource | undefined,
  canDeleteFiles: boolean,
): PrinterFileBulkCapabilities {
  if (!source || entry.availability !== 'Available') return NO_BULK_ACTIONS;
  return {
    copy: source.canCopy,
    download: canDownloadSource(source),
    delete: source.canDelete && canDeleteFiles,
  };
}

export function hasBulkCapability(capabilities: PrinterFileBulkCapabilities): boolean {
  return capabilities.copy || capabilities.download || capabilities.delete;
}

/** Server reason codes meaning the listed observation no longer matches the printer. */
const STALE_REASON_CODES = new Set(['stale_observation', 'source_changed_reconfirm', 'source_observation_changed']);

export const CHANGED_ON_PRINTER_MESSAGE = 'The file changed on the printer. The list was refreshed; try again.';

export interface PrinterFileFailure {
  message: string;
  /** True when the listed observation revision is out of date. */
  stale: boolean;
}

/**
 * Interprets a rejected printer-file request (the shared ApiError shape). JSON responses carry
 * `{ code }` or ProblemDetails `reasonCode`; blob downloads have no parsed body, so a 409 there
 * means the observation changed.
 */
export function describePrinterFileFailure(error: unknown, { blobResponse = false } = {}): PrinterFileFailure {
  const failure = error as {
    statusCode?: number;
    message?: string;
    data?: { code?: unknown; reasonCode?: unknown } | null;
  } | null;
  const reasonCode = failure?.data?.reasonCode;
  const code = typeof reasonCode === 'string' ? reasonCode
    : typeof failure?.data?.code === 'string' ? failure.data.code : null;
  const stale = (code !== null && STALE_REASON_CODES.has(code)) || (blobResponse && failure?.statusCode === 409);
  if (stale) return { message: CHANGED_ON_PRINTER_MESSAGE, stale };
  return { message: describeReasonCode(code) ?? (failure?.message || 'Unknown error'), stale };
}