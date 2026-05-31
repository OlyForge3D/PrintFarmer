/** NFC SignalR event payloads and API DTOs — camelCase matching backend serialization */

export interface NfcTagReadEvent {
  tagUid: string;
  printerId: string;
  spoolId?: string;
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
  spoolId?: string;
  trayId?: string;
}

export interface NfcBindingDto {
  id: string;
  tagUid: string;
  printerId: string;
  printerName?: string;
  spoolId?: string;
  spoolName?: string;
  trayId?: string;
  spoolLastSeenAt?: string;
  createdAt: string;
}
