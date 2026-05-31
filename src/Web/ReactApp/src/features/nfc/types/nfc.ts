/** NFC SignalR event payloads and API DTOs — camelCase matching backend serialization */

export interface NfcTagReadEvent {
  tagUid: string;
  printerId: string;
  spoolId?: number | null;
  trayId?: string;
  spoolLastSeenAt?: string;
}

export interface NfcTagUnknownEvent {
  tagUid: string;
  printerId: string;
}

export interface NfcLinkRequest {
  tagUid: string;
  printerId: string;
  spoolId?: number | null;
  trayId?: string;
}

export interface NfcBindingDto {
  id: string;
  tagUid: string;
  printerId: string;
  printerName?: string;
  spoolId?: number | null;
  spoolName?: string;
  trayId?: string;
  spoolLastSeenAt?: string;
  createdAt: string;
}

/**
 * Parse a spool ID string to a number suitable for API submission.
 * Returns undefined for empty/whitespace strings, or NaN-producing inputs.
 */
export function parseSpoolId(value: string | undefined | null): number | undefined {
  if (value == null || value.trim() === '') return undefined;
  const parsed = parseInt(value, 10);
  return Number.isNaN(parsed) ? undefined : parsed;
}
