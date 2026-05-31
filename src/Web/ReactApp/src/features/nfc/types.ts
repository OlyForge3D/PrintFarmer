/** Payload for `nfctagunknown` SignalR event — an unrecognized tag was scanned */
export interface NfcTagUnknownEvent {
  tagUid: string;
  deviceId: string;
  deviceName?: string;
  printerId?: string;
  printerName?: string;
  scannedAt: string;
}

/** Payload for `nfctagknown` SignalR event — a recognized tag was scanned */
export interface NfcTagKnownEvent {
  tagUid: string;
  spoolId: number;
  spoolName?: string;
  deviceId: string;
  deviceName?: string;
  scannedAt: string;
}

/** Payload for `nfctagmismatch` — tag is bound to a different spool than expected */
export interface NfcTagMismatchEvent {
  tagUid: string;
  deviceId: string;
  deviceName?: string;
  currentSpoolId: number;
  currentSpoolName?: string;
  expectedSpoolId?: number;
  expectedSpoolName?: string;
  scannedAt: string;
}

/** Payload for `nfcreaderoffline` — reader device went offline */
export interface NfcReaderOfflineEvent {
  deviceId: string;
  deviceName?: string;
}

/** Request body for POST /api/nfc/link */
export interface NfcLinkRequest {
  tagUid: string;
  spoolId: number;
  deviceId: string;
}

/** Response from POST /api/nfc/link */
export interface NfcLinkResponse {
  success: boolean;
  message?: string;
}

export type NfcPairingStep =
  | 'scanning'
  | 'detected'
  | 'search'
  | 'confirm'
  | 'success'
  | 'error';
