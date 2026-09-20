/**
 * Payload for `nfctagunknown` from /hubs/nfc — unrecognized tag, no binding found.
 * Matches NfcTagService.cs payload: { tagUid, printerId, readAt }
 */
export interface NfcTagUnknownEvent {
  tagUid: string;
  printerId?: string;
  readAt: string;
}

/**
 * Payload for `nfctagread` from /hubs/nfc — known tag scanned, binding exists.
 * Matches NfcTagService.cs payload: { tagUid, spoolId, spoolName, printerId, trayId, readAt }
 */
export interface NfcTagReadEvent {
  tagUid: string;
  spoolId: number;
  spoolName?: string;
  printerId?: string;
  trayId?: string;
  readAt: string;
}

/** Request body for POST /api/nfc/link (matches LinkNfcTagRequest C# DTO) */
export interface NfcLinkRequest {
  tagUid: string;
  spoolId?: number | null;
  spoolName?: string;
  printerId?: string;
  trayId?: string;
  /** Timestamp from the scan that initiated this binding. */
  readAt?: string;
}

/** Response from POST /api/nfc/link */
export interface NfcLinkResponse {
  id: string;
  tagUid: string;
  spoolId?: number;
  spoolName?: string;
  printerId?: string;
  printerName?: string;
  trayId?: string;
  spoolLastSeenAt?: string;
  createdAt: string;
  updatedAt?: string;
}

/** Response shape for NFC tag bindings listed by GET /api/nfc/bindings. */
export type NfcBindingDto = NfcLinkResponse;

export type NfcPairingStep =
  | 'scanning'
  | 'detected'
  | 'search'
  | 'confirm'
  | 'success'
  | 'error'
  | 'unavailable';
