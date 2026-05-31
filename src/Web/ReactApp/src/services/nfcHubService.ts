import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { getHubUrl } from '@/common/utils/apiUrlHelpers';
import type { NfcTagReadEvent, NfcTagUnknownEvent } from '@/features/nfc/types';

type NfcTagReadCallback = (event: NfcTagReadEvent) => void;
type NfcTagUnknownCallback = (event: NfcTagUnknownEvent) => void;
type ConnectionChangedCallback = (connected: boolean) => void;

/**
 * Singleton service for the /hubs/nfc SignalR hub (PR #383 contract).
 * Emits: nfctagread (known tag), nfctagunknown (no binding found).
 */
class NfcHubService {
  private connection: HubConnection | null = null;
  private connected = false;
  private connecting = false;

  private tagReadCallbacks: NfcTagReadCallback[] = [];
  private tagUnknownCallbacks: NfcTagUnknownCallback[] = [];
  private connectionCallbacks: ConnectionChangedCallback[] = [];

  async ensureConnected(): Promise<void> {
    if (!this.connection) {
      this.buildConnection();
    }
    const conn = this.connection!;
    if (conn.state === HubConnectionState.Connected) return;
    if (conn.state === HubConnectionState.Connecting) return;
    if (conn.state !== HubConnectionState.Disconnected) return;
    if (this.connecting) return;
    this.connecting = true;
    try {
      await conn.start();
      this.setConnected(true);
    } catch (err) {
      console.error('[nfcHub] connect failed:', err);
      this.setConnected(false);
    } finally {
      this.connecting = false;
    }
  }

  isConnected(): boolean {
    return this.connected;
  }

  onTagRead(callback: NfcTagReadCallback): () => void {
    this.tagReadCallbacks.push(callback);
    return () => {
      const i = this.tagReadCallbacks.indexOf(callback);
      if (i > -1) this.tagReadCallbacks.splice(i, 1);
    };
  }

  onTagUnknown(callback: NfcTagUnknownCallback): () => void {
    this.tagUnknownCallbacks.push(callback);
    return () => {
      const i = this.tagUnknownCallbacks.indexOf(callback);
      if (i > -1) this.tagUnknownCallbacks.splice(i, 1);
    };
  }

  onConnectionChanged(callback: ConnectionChangedCallback): () => void {
    this.connectionCallbacks.push(callback);
    return () => {
      const i = this.connectionCallbacks.indexOf(callback);
      if (i > -1) this.connectionCallbacks.splice(i, 1);
    };
  }

  private buildConnection(): void {
    const url = getHubUrl('/hubs/nfc');
    this.connection = new HubConnectionBuilder()
      .withUrl(url)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    this.connection.on('nfctagread', (event: NfcTagReadEvent) => {
      this.tagReadCallbacks.forEach((cb) => {
        try { cb(event); } catch (e) { console.error('[nfcHub] tagread cb error:', e); }
      });
    });

    this.connection.on('nfctagunknown', (event: NfcTagUnknownEvent) => {
      this.tagUnknownCallbacks.forEach((cb) => {
        try { cb(event); } catch (e) { console.error('[nfcHub] tagunknown cb error:', e); }
      });
    });

    this.connection.onclose(() => this.setConnected(false));
    this.connection.onreconnecting(() => this.setConnected(false));
    this.connection.onreconnected(() => this.setConnected(true));
  }

  private setConnected(value: boolean): void {
    this.connected = value;
    this.connectionCallbacks.forEach((cb) => {
      try { cb(value); } catch (e) { console.error('[nfcHub] connection cb error:', e); }
    });
  }
}

export const nfcHubService = new NfcHubService();
