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
type ConnectionStateCallback = (connected: boolean) => void;

class NfcSignalRService {
  private connection: HubConnection | null = null;
  private tagReadCallbacks: NfcTagReadCallback[] = [];
  private tagUnknownCallbacks: NfcTagUnknownCallback[] = [];
  private connectionStateCallbacks: ConnectionStateCallback[] = [];
  private connecting = false;

  get connectionState(): HubConnectionState {
    return this.connection?.state ?? HubConnectionState.Disconnected;
  }

  get isConnected(): boolean {
    return this.connection?.state === HubConnectionState.Connected;
  }

  async connect(): Promise<void> {
    if (this.connection?.state === HubConnectionState.Connected || this.connecting) return;
    this.connecting = true;

    try {
      if (!this.connection) {
        this.connection = new HubConnectionBuilder()
          .withUrl(getHubUrl('/hubs/nfc'))
          .withAutomaticReconnect()
          .configureLogging(LogLevel.Warning)
          .build();

        this.setupEventHandlers();
      }

      await this.connection.start();
      this.notifyConnectionState(true);
    } catch (err) {
      console.error('[NfcSignalR] Connection failed:', err);
    } finally {
      this.connecting = false;
    }
  }

  private setupEventHandlers(): void {
    if (!this.connection) return;

    this.connection.on('nfctagread', (event: NfcTagReadEvent) => {
      this.tagReadCallbacks.forEach((cb) => {
        try { cb(event); } catch (e) { console.error('[NfcSignalR] tagRead cb error:', e); }
      });
    });

    this.connection.on('nfctagunknown', (event: NfcTagUnknownEvent) => {
      this.tagUnknownCallbacks.forEach((cb) => {
        try { cb(event); } catch (e) { console.error('[NfcSignalR] tagUnknown cb error:', e); }
      });
    });

    this.connection.onclose(() => this.notifyConnectionState(false));
    this.connection.onreconnecting(() => this.notifyConnectionState(false));
    this.connection.onreconnected(() => this.notifyConnectionState(true));
  }

  private notifyConnectionState(connected: boolean): void {
    this.connectionStateCallbacks.forEach((cb) => {
      try { cb(connected); } catch { /* ignore */ }
    });
  }

  onTagRead(callback: NfcTagReadCallback): () => void {
    this.tagReadCallbacks.push(callback);
    return () => {
      this.tagReadCallbacks = this.tagReadCallbacks.filter((cb) => cb !== callback);
    };
  }

  onTagUnknown(callback: NfcTagUnknownCallback): () => void {
    this.tagUnknownCallbacks.push(callback);
    return () => {
      this.tagUnknownCallbacks = this.tagUnknownCallbacks.filter((cb) => cb !== callback);
    };
  }

  onConnectionStateChange(callback: ConnectionStateCallback): () => void {
    this.connectionStateCallbacks.push(callback);
    return () => {
      this.connectionStateCallbacks = this.connectionStateCallbacks.filter((cb) => cb !== callback);
    };
  }
}

export const nfcSignalRService = new NfcSignalRService();
