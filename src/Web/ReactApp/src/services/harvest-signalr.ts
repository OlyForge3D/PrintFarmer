import { 
  HubConnection, 
  HubConnectionBuilder, 
  HubConnectionState,
  LogLevel 
} from '@microsoft/signalr';
import { PrinterStatusUpdate, DiscoveryProgressDto, DiscoveryPrinterFoundDto, DiscoveryCompletedDto, HarvestUpdateDto, JobQueueUpdateDto } from '@/types/api';
import { apiClient } from '@/services/api';

type PrinterStatusCallback = (status: PrinterStatusUpdate) => void;
type HarvestUpdateCallback = (operationId: string, status: HarvestUpdateDto) => void;
export type HarvestFileProgress = {
  operationId: string;
  fileName: string;
  bytesCopied: number;
  totalBytes: number;
  percent: number;
};
type HarvestFileProgressCallback = (progress: HarvestFileProgress) => void;
type JobQueueUpdateCallback = (update: JobQueueUpdateDto) => void;
type ConnectionStateCallback = (connected: boolean) => void;
type DiscoveryProgressCallback = (progress: DiscoveryProgressDto) => void;
type DiscoveryPrinterFoundCallback = (found: DiscoveryPrinterFoundDto) => void;
type DiscoveryCompletedCallback = (completed: DiscoveryCompletedDto) => void;
// HarvestFileDiscovered event type
export type HarvestFileDiscoveredEvent = {
  operationId: string;
  fileId: string;
  fileName: string;
  filePath: string;
  fileSize: number;
  status?: string;
  error?: string;
};
type HarvestFileDiscoveredCallback = (evt: HarvestFileDiscoveredEvent) => void;

export class SignalRService {
  private connection: HubConnection | null = null;
  private discoveryConnection: HubConnection | null = null;
  private harvestFileDiscoveredCallbacks: HarvestFileDiscoveredCallback[] = [];
  private reconnectAttempts = 0;
  private maxReconnectAttempts = 5;
  private reconnectDelay = 1000; // Start with 1 second
  private maxReconnectDelay = 30000; // Max 30 seconds
  private signalrSettings: { logLevel: string; consoleLoggingEnabled: boolean } | null = null;

  // Event handlers
  private printerStatusCallbacks: PrinterStatusCallback[] = [];
  private harvestUpdateCallbacks: HarvestUpdateCallback[] = [];
  private harvestFileProgressCallbacks: HarvestFileProgressCallback[] = [];
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
      case 'critical':
        return LogLevel.Critical;
      case 'error':
        return LogLevel.Error;
      case 'warning':
        return LogLevel.Warning;
      case 'information':
        return LogLevel.Information;
      case 'debug':
        return LogLevel.Debug;
      case 'trace':
        return LogLevel.Trace;
      case 'none':
        return LogLevel.None;
      default:
        return LogLevel.Information;
    }
  }

  private buildConnection(): void {
    // Use harvest hub for harvest events, printers hub for discovery
    const harvestSignalrUrl = import.meta.env.VITE_SIGNALR_HARVEST_URL || 'http://localhost:5245/hubs/harvest';
    console.info('[SignalR] Building harvest connection with URL:', harvestSignalrUrl);
    this.connection = new HubConnectionBuilder()
      .withUrl(harvestSignalrUrl)
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
    // Set up event handlers
    this.setupEventHandlers();

  }


  // ============ Harvest File Discovered Event Subscription ============
  onHarvestFileDiscovered(callback: HarvestFileDiscoveredCallback): () => void {
    this.harvestFileDiscoveredCallbacks.push(callback);
    return () => {
      const index = this.harvestFileDiscoveredCallbacks.indexOf(callback);
      if (index > -1) {
        this.harvestFileDiscoveredCallbacks.splice(index, 1);
      }
    };
  }

  // Connection lifecycle events
  private setupEventHandlers(): void {
    if (!this.connection) return;
    // Harvest events (harvest hub)
  this.connection.on('harvestfilediscovered', (evt: HarvestFileDiscoveredEvent) => {
      this.harvestFileDiscoveredCallbacks.forEach(callback => {
        try {
          callback(evt);
        } catch (error) {
          console.error('Error in HarvestFileDiscovered callback:', error);
        }
      });
    });

    this.connection.onclose((error) => {
      console.warn('SignalR connection closed', error);
      this.notifyConnectionState(false);
    });

    this.connection.onreconnecting((error) => {
      console.info('SignalR reconnecting...', error);
      this.notifyConnectionState(false);
    });

    this.connection.onreconnected((connectionId) => {
      console.info('SignalR reconnected', connectionId);
      this.reconnectAttempts = 0;
      this.notifyConnectionState(true);
    });

    // Business event handlers (harvest hub)
  this.connection.on('printerupdated', (status: PrinterStatusUpdate) => {
      this.printerStatusCallbacks.forEach(callback => {
        try {
          callback(status);
        } catch (error) {
          console.error('Error in printer status callback:', error);
        }
      });
    });

  this.connection.on('harvestupdate', (operationId: string, status: HarvestUpdateDto) => {
      this.harvestUpdateCallbacks.forEach(callback => {
        try {
          callback(operationId, status);
        } catch (error) {
          console.error('Error in harvest update callback:', error);
        }
      });
    });

    // NEW: Per-file progress event
  this.connection.on('harvestfileprogress', (progress: HarvestFileProgress) => {
      this.harvestFileProgressCallbacks.forEach(callback => {
        try {
          callback(progress);
        } catch (error) {
          console.error('Error in harvest file progress callback:', error);
        }
      });
    });

  this.connection.on('jobqueueupdate', (update: JobQueueUpdateDto) => {
      this.jobQueueUpdateCallbacks.forEach(callback => {
        try {
          callback(update);
        } catch (error) {
          console.error('Error in job queue update callback:', error);
        }
      });
    });

    // Discovery event handlers (printers hub)
  }
  // ============ Harvest File Progress Event Subscription ============
  onHarvestFileProgress(callback: HarvestFileProgressCallback): () => void {
    this.harvestFileProgressCallbacks.push(callback);
    return () => {
      const index = this.harvestFileProgressCallbacks.indexOf(callback);
      if (index > -1) {
        this.harvestFileProgressCallbacks.splice(index, 1);
      }
    };
  }

  // ============ Harvest Group Methods ============
  // Helper to parse string to Guid (if not already Guid)
  private toGuid(id: string): string {
    // If already in Guid format, return as is
    if (/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(id)) {
      return id;
    }
    // Otherwise, try to parse or throw
    throw new Error('Invalid operationId for SignalR group: ' + id);
  }

  async joinHarvestGroup(operationId: string): Promise<void> {
    if (this.connection && this.connection.state === HubConnectionState.Connected) {
      await this.connection.invoke('JoinHarvestGroupAsync', this.toGuid(operationId));
    }
  }

  async leaveHarvestGroup(operationId: string): Promise<void> {
    if (this.connection && this.connection.state === HubConnectionState.Connected) {
      await this.connection.invoke('LeaveHarvestGroupAsync', this.toGuid(operationId));
    }
  }

  private notifyConnectionState(connected: boolean): void {
    this.connectionStateCallbacks.forEach(callback => {
      try {
        callback(connected);
      } catch (error) {
        console.error('Error in connection state callback:', error);
      }
    });
  }

  async connect(): Promise<void> {
    if (!this.connection || !this.discoveryConnection) {
      this.buildConnection();
    }

    // Start main connection if needed
    if (this.connection) {
      if (this.connection.state === HubConnectionState.Disconnected) {
        try {
          await this.connection.start();
          console.info('SignalR (harvest) connected');
          this.reconnectAttempts = 0;
          this.notifyConnectionState(true);
        } catch (error) {
          console.error('SignalR (harvest) connection failed:', error);
          this.notifyConnectionState(false);
          // Retry with exponential backoff
          if (this.reconnectAttempts < this.maxReconnectAttempts) {
            const delay = Math.min(
              this.reconnectDelay * Math.pow(2, this.reconnectAttempts),
              this.maxReconnectDelay
            );
            this.reconnectAttempts++;
            console.info(`Retrying SignalR (harvest) connection in ${delay}ms (attempt ${this.reconnectAttempts})`);
            setTimeout(() => this.connect(), delay);
          } else {
            console.error('Max reconnection attempts reached');
          }
        }
      } else if (this.connection.state === HubConnectionState.Connected) {
        console.info('SignalR (harvest) already connected');
      } else if (this.connection.state === HubConnectionState.Connecting) {
        console.info('SignalR (harvest) already connecting, waiting...');
      } else {
        console.warn('SignalR (harvest) connection is in unexpected state:', this.connection.state);
      }
    }

    // Start discovery connection if needed
    if (this.discoveryConnection) {
      if (this.discoveryConnection.state === HubConnectionState.Disconnected) {
        try {
          await this.discoveryConnection.start();
          console.info('SignalR (discovery) connected');
        } catch (error) {
          console.error('SignalR (discovery) connection failed:', error);
        }
      } else if (this.discoveryConnection.state === HubConnectionState.Connected) {
        console.info('SignalR (discovery) already connected');
      } else if (this.discoveryConnection.state === HubConnectionState.Connecting) {
        console.info('SignalR (discovery) already connecting, waiting...');
      } else {
        console.warn('SignalR (discovery) connection is in unexpected state:', this.discoveryConnection.state);
      }
    }
  }

  async disconnect(): Promise<void> {
    if (this.connection && this.connection.state === HubConnectionState.Connected) {
      await this.connection.stop();
      console.info('SignalR disconnected');
      this.notifyConnectionState(false);
    }
  }

  // ============ Event Subscription Methods ============

  onPrinterStatusUpdate(callback: PrinterStatusCallback): () => void {
    this.printerStatusCallbacks.push(callback);
    
    // Return unsubscribe function
    return () => {
      const index = this.printerStatusCallbacks.indexOf(callback);
      if (index > -1) {
        this.printerStatusCallbacks.splice(index, 1);
      }
    };
  }

  onHarvestUpdate(callback: HarvestUpdateCallback): () => void {
    this.harvestUpdateCallbacks.push(callback);
    
    return () => {
      const index = this.harvestUpdateCallbacks.indexOf(callback);
      if (index > -1) {
        this.harvestUpdateCallbacks.splice(index, 1);
      }
    };
  }

  onJobQueueUpdate(callback: JobQueueUpdateCallback): () => void {
    this.jobQueueUpdateCallbacks.push(callback);
    
    return () => {
      const index = this.jobQueueUpdateCallbacks.indexOf(callback);
      if (index > -1) {
        this.jobQueueUpdateCallbacks.splice(index, 1);
      }
    };
  }

  onConnectionStateChange(callback: ConnectionStateCallback): () => void {
    this.connectionStateCallbacks.push(callback);
    
    return () => {
      const index = this.connectionStateCallbacks.indexOf(callback);
      if (index > -1) {
        this.connectionStateCallbacks.splice(index, 1);
      }
    };
  }

  // ============ Discovery Event Subscriptions ============

  onDiscoveryProgress(callback: DiscoveryProgressCallback): () => void {
    this.discoveryProgressCallbacks.push(callback);
    
    return () => {
      const index = this.discoveryProgressCallbacks.indexOf(callback);
      if (index > -1) {
        this.discoveryProgressCallbacks.splice(index, 1);
      }
    };
  }

  onDiscoveryPrinterFound(callback: DiscoveryPrinterFoundCallback): () => void {
    this.discoveryPrinterFoundCallbacks.push(callback);
    
    return () => {
      const index = this.discoveryPrinterFoundCallbacks.indexOf(callback);
      if (index > -1) {
        this.discoveryPrinterFoundCallbacks.splice(index, 1);
      }
    };
  }

  onDiscoveryCompleted(callback: DiscoveryCompletedCallback): () => void {
    this.discoveryCompletedCallbacks.push(callback);
    
    return () => {
      const index = this.discoveryCompletedCallbacks.indexOf(callback);
      if (index > -1) {
        this.discoveryCompletedCallbacks.splice(index, 1);
      }
    };
  }

  // ============ Server Method Calls ============

  async joinPrinterGroup(printerId: string): Promise<void> {
    if (this.connection && this.connection.state === HubConnectionState.Connected) {
      await this.connection.invoke('JoinPrinterGroup', printerId);
    }
  }

  async leavePrinterGroup(printerId: string): Promise<void> {
    if (this.connection && this.connection.state === HubConnectionState.Connected) {
      await this.connection.invoke('LeavePrinterGroup', printerId);
    }
  }

  async requestPrinterStatus(printerId: string): Promise<void> {
    if (this.connection && this.connection.state === HubConnectionState.Connected) {
      await this.connection.invoke('RequestPrinterStatus', printerId);
    }
  }

  // ============ Utility Methods ============

  get connectionState(): HubConnectionState {
    return this.connection?.state ?? HubConnectionState.Disconnected;
  }

  get isConnected(): boolean {
    return this.connection?.state === HubConnectionState.Connected;
  }

  get connectionId(): string | null {
    return this.connection?.connectionId ?? null;
  }

  // Refresh SignalR settings and reconnect with new log level
  async refreshSettings(): Promise<void> {
    await this.loadSettings();
    
    // If connection exists and is connected, recreate it with new settings
    if (this.connection && this.connection.state === HubConnectionState.Connected) {
      await this.connection.stop();
      this.buildConnection();
      await this.connect();
    }
  }

  // ============ Discovery Group Methods ============


  // Clean up all resources
  dispose(): void {
    this.printerStatusCallbacks = [];
  this.harvestUpdateCallbacks = [];
  this.harvestFileProgressCallbacks = [];
    this.harvestFileDiscoveredCallbacks = [];
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

// Export singleton instance
export const signalRService = new SignalRService();