import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel
} from '@microsoft/signalr';
import { PrinterStatusUpdate, JobQueueUpdateDto, DiscoveryProgressDto, DiscoveryPrinterFoundDto, DiscoveryCompletedDto } from '@/types/api';
import { apiClient } from '@/services/api';

type PrinterStatusCallback = (status: PrinterStatusUpdate) => void;
type JobQueueUpdateCallback = (update: JobQueueUpdateDto) => void;
type ConnectionStateCallback = (connected: boolean) => void;
type DiscoveryProgressCallback = (progress: DiscoveryProgressDto) => void;
type DiscoveryPrinterFoundCallback = (found: DiscoveryPrinterFoundDto) => void;
type DiscoveryCompletedCallback = (completed: DiscoveryCompletedDto) => void;

export class PrinterSignalRService {

  private buildConnection(): void {
    const printersSignalrUrl = import.meta.env.VITE_SIGNALR_PRINTERS_URL || 'http://localhost:5245/hubs/printers';
    this.connection = new HubConnectionBuilder()
      .withUrl(printersSignalrUrl)
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          const delay = Math.min(
            this.reconnectDelay * Math.pow(2, retryContext.previousRetryCount),
            this.maxReconnectDelay
          );
          const jitter = delay * 0.1 * (Math.random() - 0.5);
          return Math.max(1000, delay + jitter);
        }
      })
      .configureLogging(this.getLogLevel())
      .build();
    this.setupEventHandlers();
  }

  private setupEventHandlers(): void {
    if (!this.connection) return;
    this.connection.on('printerupdated', (status: PrinterStatusUpdate) => {
      this.printerStatusCallbacks.forEach(cb => {
        try { cb(status); } catch (e) { console.error('Printer status cb error:', e); }
      });
    });
    this.connection.on('jobqueueupdate', (update: JobQueueUpdateDto) => {
      this.jobQueueUpdateCallbacks.forEach(cb => {
        try { cb(update); } catch (e) { console.error('Job queue cb error:', e); }
      });
    });
    // Discovery events
    this.connection.on('discoveryprogress', (progress: DiscoveryProgressDto) => {
      this.discoveryProgressCallbacks.forEach(cb => {
        try { cb(progress); } catch (e) { console.error('Discovery progress cb error:', e); }
      });
    });
    this.connection.on('discoveryprinterfound', (found: DiscoveryPrinterFoundDto) => {
      this.discoveryPrinterFoundCallbacks.forEach(cb => {
        try { cb(found); } catch (e) { console.error('Discovery printer found cb error:', e); }
      });
    });
    this.connection.on('discoverycompleted', (completed: DiscoveryCompletedDto) => {
      this.discoveryCompletedCallbacks.forEach(cb => {
        try { cb(completed); } catch (e) { console.error('Discovery completed cb error:', e); }
      });
    });
    this.connection.onclose(() => this.notifyConnectionState(false));
    this.connection.onreconnecting(() => this.notifyConnectionState(false));
    this.connection.onreconnected(() => {
      this.reconnectAttempts = 0;
      this.notifyConnectionState(true);
    });
  }
  private connection: HubConnection | null = null;
  private reconnectAttempts = 0;
  private maxReconnectAttempts = 5;
  private reconnectDelay = 1000;
  private maxReconnectDelay = 30000;
  private signalrSettings: { logLevel: string; consoleLoggingEnabled: boolean } | null = null;

  private printerStatusCallbacks: PrinterStatusCallback[] = [];
  private jobQueueUpdateCallbacks: JobQueueUpdateCallback[] = [];
  private connectionStateCallbacks: ConnectionStateCallback[] = [];
  private discoveryProgressCallbacks: DiscoveryProgressCallback[] = [];
  private discoveryPrinterFoundCallbacks: DiscoveryPrinterFoundCallback[] = [];
  private discoveryCompletedCallbacks: DiscoveryCompletedCallback[] = [];

  constructor() {
    this.loadSettings().then(() => {
      this.buildConnection();
    });
  }

  private async loadSettings(): Promise<void> {
    try {
      this.signalrSettings = await apiClient.getSignalRSettings();
    } catch (error) {
      console.warn('Failed to load SignalR settings, using defaults:', error);
      this.signalrSettings = { logLevel: 'Information', consoleLoggingEnabled: true };
    }
  }

  private getLogLevel(): LogLevel {
    if (!this.signalrSettings?.consoleLoggingEnabled) {
      return LogLevel.None;
    }
    switch (this.signalrSettings.logLevel?.toLowerCase()) {
      case 'critical': return LogLevel.Critical;
      case 'error': return LogLevel.Error;
      case 'warning': return LogLevel.Warning;
      case 'information': return LogLevel.Information;
      case 'debug': return LogLevel.Debug;
      case 'trace': return LogLevel.Trace;
      case 'none': return LogLevel.None;
      default: return LogLevel.Information;
    }
  }

  private notifyConnectionState(connected: boolean): void {
    this.connectionStateCallbacks.forEach(cb => {
      try { cb(connected); } catch (e) { console.error('Connection state cb error:', e); }
    });
  }

      public async connect(): Promise<void> {
        if (!this.connection) this.buildConnection();
        if (this.connection!.state === HubConnectionState.Connected) return;
        if (this.connection!.state === HubConnectionState.Connecting) return;
        if (this.connection!.state !== HubConnectionState.Disconnected) return;
        try {
          await this.connection!.start();
          this.reconnectAttempts = 0;
          this.notifyConnectionState(true);
        } catch {
          this.notifyConnectionState(false);
          if (this.reconnectAttempts < this.maxReconnectAttempts) {
            const delay = Math.min(
              this.reconnectDelay * Math.pow(2, this.reconnectAttempts),
              this.maxReconnectDelay
            );
            this.reconnectAttempts++;
            setTimeout(() => this.connect(), delay);
          }
        }
      }

      // Discovery event subscriptions
      public onDiscoveryProgress(callback: DiscoveryProgressCallback): () => void {
        this.discoveryProgressCallbacks.push(callback);
        return () => {
          const idx = this.discoveryProgressCallbacks.indexOf(callback);
          if (idx > -1) this.discoveryProgressCallbacks.splice(idx, 1);
        };
      }
      public onDiscoveryPrinterFound(callback: DiscoveryPrinterFoundCallback): () => void {
        this.discoveryPrinterFoundCallbacks.push(callback);
        return () => {
          const idx = this.discoveryPrinterFoundCallbacks.indexOf(callback);
          if (idx > -1) this.discoveryPrinterFoundCallbacks.splice(idx, 1);
        };
      }
      public onDiscoveryCompleted(callback: DiscoveryCompletedCallback): () => void {
        this.discoveryCompletedCallbacks.push(callback);
        return () => {
          const idx = this.discoveryCompletedCallbacks.indexOf(callback);
          if (idx > -1) this.discoveryCompletedCallbacks.splice(idx, 1);
        };
      }

      // Discovery group methods
      public async joinDiscoveryGroup(sessionId: string): Promise<void> {
        if (this.connection && this.connection.state === HubConnectionState.Connected) {
          await this.connection.invoke('JoinDiscoveryGroupAsync', sessionId);
        }
      }
      public async leaveDiscoveryGroup(sessionId: string): Promise<void> {
        if (this.connection && this.connection.state === HubConnectionState.Connected) {
          await this.connection.invoke('LeaveDiscoveryGroupAsync', sessionId);
        }
      }

      public async disconnect(): Promise<void> {
        if (this.connection && this.connection.state === HubConnectionState.Connected) {
          await this.connection.stop();
          }
      }


  onPrinterStatusUpdate(callback: PrinterStatusCallback): () => void {
    this.printerStatusCallbacks.push(callback);
    return () => {
      const idx = this.printerStatusCallbacks.indexOf(callback);
      if (idx > -1) this.printerStatusCallbacks.splice(idx, 1);
    };
  }

  onJobQueueUpdate(callback: JobQueueUpdateCallback): () => void {
    this.jobQueueUpdateCallbacks.push(callback);
    return () => {
      const idx = this.jobQueueUpdateCallbacks.indexOf(callback);
      if (idx > -1) this.jobQueueUpdateCallbacks.splice(idx, 1);
    };
  }

  onConnectionStateChange(callback: ConnectionStateCallback): () => void {
    this.connectionStateCallbacks.push(callback);
    return () => {
      const idx = this.connectionStateCallbacks.indexOf(callback);
      if (idx > -1) this.connectionStateCallbacks.splice(idx, 1);
    };
  }

  get connectionState(): HubConnectionState {
    return this.connection?.state ?? HubConnectionState.Disconnected;
  }
  get isConnected(): boolean {
    return this.connection?.state === HubConnectionState.Connected;
  }
  get connectionId(): string | null {
    return this.connection?.connectionId ?? null;
  }
  dispose(): void {
    this.printerStatusCallbacks = [];
    this.jobQueueUpdateCallbacks = [];
    this.connectionStateCallbacks = [];
    this.discoveryProgressCallbacks = [];
    this.discoveryPrinterFoundCallbacks = [];
    this.discoveryCompletedCallbacks = [];
    if (this.connection) {
      this.connection.stop();
      this.connection = null;
    }
  }
}

export const printerSignalRService = new PrinterSignalRService();




