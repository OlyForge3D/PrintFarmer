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
  spoolId: number;
  printerId?: string;
  trayId?: string;
}

/** Response from POST /api/nfc/link */
export interface NfcLinkResponse {
  tagUid: string;
  spoolId?: number;
  printerId?: string;
  trayId?: string;
  createdAt: string;
  updatedAt: string;
}

export type NfcPairingStep =
  | 'scanning'
  | 'detected'
  | 'search'
  | 'confirm'
  | 'success'
  | 'error'
  | 'unavailable';
