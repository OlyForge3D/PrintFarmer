import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel
} from '@microsoft/signalr';
import { HarvestUpdateDto, JobQueueUpdateDto, PrinterStatusUpdate } from '@/types/api';
import { apiClient } from '@/services/api';
import { getHubUrl } from '@/common/utils/apiUrlHelpers';

type PrinterStatusCallback = (status: PrinterStatusUpdate) => void;
type HarvestUpdateCallback = (operationId: string, status: HarvestUpdateDto) => void;
export type HarvestFileProgress = {
  operationId: string;
  fileName: string;
  bytesCopied: number;
  totalBytes: number;
  percent: number;
};
export type HarvestOperationProgress = {
  operationId: string;
  filesFound: number;
  filesProcessed: number;
  filesAdded: number;
  filesSkipped: number;
  filesErrored: number;
};
export type HarvestOperationCompletedEvent = {
  operationId: string;
  status: string;
  filesAdded: number;
  filesSkipped: number;
  filesErrored: number;
  completedAt: string;
};
type HarvestFileProgressCallback = (progress: HarvestFileProgress) => void;
type HarvestOperationProgressCallback = (progress: HarvestOperationProgress) => void;
type HarvestOperationCompletedCallback = (evt: HarvestOperationCompletedEvent) => void;
type JobQueueUpdateCallback = (update: JobQueueUpdateDto) => void;
type ConnectionStateCallback = (connected: boolean) => void;
// HarvestFileDiscovered event type
export type HarvestFileDiscoveredEvent = {
  operationId: string;
  fileId: string;
  fileName: string;
  filePath: string;
  fileSize: number;
  modifiedAt?: string;
  status?: string;
  error?: string;
  thumbnailUrl?: string;
  extractedSlicer?: string;
  extractedSlicerVersion?: string;
  extractedMaterial?: string;
  extractedNozzleDiameter?: number;
  extractedPrintTime?: number;
  extractedFilamentLength?: number;
};
type HarvestFileDiscoveredCallback = (evt: HarvestFileDiscoveredEvent) => void;

// Harvest file updated event type (includes status and error information)
export type HarvestFileUpdatedEvent = {
  id: string;
  operationId: string;
  fileName: string;
  filePath: string;
  fileSize: number;
  status: string;
  error?: string;
  completedAt?: string;
  thumbnailUrl?: string;
  extractedSlicerName?: string;
  extractedSlicerVersion?: string;
  extractedMaterial?: string;
  extractedNozzleDiameter?: number;
  extractedPrintTime?: number;
  extractedFilamentLength?: number;
};
type HarvestFileUpdatedCallback = (evt: HarvestFileUpdatedEvent) => void;

// Harvest discovery restart event type
export type HarvestDiscoveryRestartedEvent = {
  operationId: string;
  status: string;
  restartedAt: string;
};
type HarvestDiscoveryRestartedCallback = (evt: HarvestDiscoveryRestartedEvent) => void;

// Harvest discovery complete event type
export type HarvestDiscoveryCompleteEvent = {
  operationId: string;
  totalFilesDiscovered: number;
  completedAt: string;
};
type HarvestDiscoveryCompleteCallback = (evt: HarvestDiscoveryCompleteEvent) => void;

export class SignalRService {
  private connection: HubConnection | null = null;
  private reconnectAttempts = 0;
  private maxReconnectAttempts = 5;
  private reconnectDelay = 1000; // Start with 1 second
  private maxReconnectDelay = 30000; // Max 30 seconds
  private signalrSettings: { logLevel: string; consoleLoggingEnabled: boolean } | null = null;

  // Event handlers
  private printerStatusCallbacks: PrinterStatusCallback[] = [];
  private harvestUpdateCallbacks: HarvestUpdateCallback[] = [];
  private harvestFileDiscoveredCallbacks: HarvestFileDiscoveredCallback[] = [];
  private harvestFileUpdatedCallbacks: HarvestFileUpdatedCallback[] = [];
  private harvestFileProgressCallbacks: HarvestFileProgressCallback[] = [];
  private harvestOperationProgressCallbacks: HarvestOperationProgressCallback[] = [];
  private harvestOperationCompletedCallbacks: HarvestOperationCompletedCallback[] = [];
  private harvestDiscoveryRestartedCallbacks: HarvestDiscoveryRestartedCallback[] = [];
  private harvestDiscoveryCompleteCallbacks: HarvestDiscoveryCompleteCallback[] = [];
  private jobQueueUpdateCallbacks: JobQueueUpdateCallback[] = [];
  private connectionStateCallbacks: ConnectionStateCallback[] = [];

  constructor() {
    this.loadSettings().then(() => {
      this.buildConnection();
    });
  }

  private async loadSettings(): Promise<void> {
    try {
      this.signalrSettings = await apiClient.getSettings<{ logLevel: string; consoleLoggingEnabled: boolean }>('SignalR');
    } catch (error) {
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
        console.warn('Failed to load SignalR settings, using defaults:', error);
      }
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
    const harvestSignalrUrl = getHubUrl('/hubs/harvest');
    if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
      console.info('[SignalR] Building harvest connection with URL:', harvestSignalrUrl);
    }
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

  // ============ Harvest File Updated Event Subscription ============
  onHarvestFileUpdated(callback: HarvestFileUpdatedCallback): () => void {
    this.harvestFileUpdatedCallbacks.push(callback);
    return () => {
      const index = this.harvestFileUpdatedCallbacks.indexOf(callback);
      if (index > -1) {
        this.harvestFileUpdatedCallbacks.splice(index, 1);
      }
    };
  }

  // ============ Harvest Operation Progress Event Subscription ============
  onHarvestOperationProgress(callback: HarvestOperationProgressCallback): () => void {
    this.harvestOperationProgressCallbacks.push(callback);
    return () => {
      const index = this.harvestOperationProgressCallbacks.indexOf(callback);
      if (index > -1) {
        this.harvestOperationProgressCallbacks.splice(index, 1);
      }
    };
  }

  // ============ Harvest Operation Completed Event Subscription ============
  onHarvestOperationCompleted(callback: HarvestOperationCompletedCallback): () => void {
    this.harvestOperationCompletedCallbacks.push(callback);
    return () => {
      const index = this.harvestOperationCompletedCallbacks.indexOf(callback);
      if (index > -1) {
        this.harvestOperationCompletedCallbacks.splice(index, 1);
      }
    };
  }

  // ============ Harvest Discovery Restarted Event Subscription ============
  onHarvestDiscoveryRestarted(callback: HarvestDiscoveryRestartedCallback): () => void {
    this.harvestDiscoveryRestartedCallbacks.push(callback);
    return () => {
      const index = this.harvestDiscoveryRestartedCallbacks.indexOf(callback);
      if (index > -1) {
        this.harvestDiscoveryRestartedCallbacks.splice(index, 1);
      }
    };
  }

  // ============ Harvest Discovery Complete Event Subscription ============
  onHarvestDiscoveryComplete(callback: HarvestDiscoveryCompleteCallback): () => void {
    this.harvestDiscoveryCompleteCallbacks.push(callback);
    return () => {
      const index = this.harvestDiscoveryCompleteCallbacks.indexOf(callback);
      if (index > -1) {
        this.harvestDiscoveryCompleteCallbacks.splice(index, 1);
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
    this.connection.on('harvestfileupdated', (evt: HarvestFileUpdatedEvent) => {
      this.harvestFileUpdatedCallbacks.forEach(callback => {
        try {
          callback(evt);
        } catch (error) {
          console.error('Error in harvestfileupdated callback:', error);
        }
      });
    });
    this.connection.onclose((error) => {
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
        console.warn('SignalR connection closed', error);
      }
      this.notifyConnectionState(false);
    });

    this.connection.onreconnecting((error) => {
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
        console.info('SignalR reconnecting...', error);
      }
      this.notifyConnectionState(false);
    });

    this.connection.onreconnected((connectionId) => {
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
        console.info('SignalR reconnected', connectionId);
      }
      this.reconnectAttempts = 0;
      this.notifyConnectionState(true);
    });

    // Business event handlers (harvest hub)
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

    // NEW: Operation progress event (overall harvest progress)
    this.connection.on('harvestoperationprogress', (progress: HarvestOperationProgress) => {
      this.harvestOperationProgressCallbacks.forEach(callback => {
        try {
          callback(progress);
        } catch (error) {
          console.error('Error in harvest operation progress callback:', error);
        }
      });
    });

    // NEW: Operation cancelled event
    this.connection.on('harvestoperationcancelled', (data: { operationId: string; status: string; completedAt: string }) => {
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
        console.info('[SignalR] Harvest operation cancelled:', data.operationId);
      }
      // Trigger operation progress callback to update UI
      this.harvestOperationProgressCallbacks.forEach(callback => {
        try {
          callback({
            operationId: data.operationId,
            filesFound: 0,
            filesProcessed: 0,
            filesAdded: 0,
            filesSkipped: 0,
            filesErrored: 0
          });
        } catch (error) {
          console.error('Error in harvest operation cancelled callback:', error);
        }
      });
    });

    // NEW: Discovery restarted event
    this.connection.on('harvestdiscoveryrestarted', (data: HarvestDiscoveryRestartedEvent) => {
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
        console.info('[SignalR] Harvest discovery restarted:', data.operationId);
      }
      // Notify all restart callbacks
      this.harvestDiscoveryRestartedCallbacks.forEach(callback => {
        try {
          callback(data);
        } catch (error) {
          console.error('Error in harvest discovery restarted callback:', error);
        }
      });
    });

    // NEW: Discovery complete event
    this.connection.on('harvestdiscoverycomplete', (data: HarvestDiscoveryCompleteEvent) => {
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
        console.info('[SignalR] Harvest discovery completed:', data.operationId, 'Total files:', data.totalFilesDiscovered);
      }
      // Notify all complete callbacks
      this.harvestDiscoveryCompleteCallbacks.forEach(callback => {
        try {
          callback(data);
        } catch (error) {
          console.error('Error in harvest discovery complete callback:', error);
        }
      });
    });

    // NEW: Operation completed event (from HarvestWorkerService)
    this.connection.on('harvestoperationcompleted', (data: HarvestOperationCompletedEvent) => {
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
        console.info('[SignalR] Harvest operation completed:', data.operationId, 'Added:', data.filesAdded, 'Skipped:', data.filesSkipped, 'Errored:', data.filesErrored);
      }
      // Notify all operation completed callbacks
      this.harvestOperationCompletedCallbacks.forEach(callback => {
        try {
          callback(data);
        } catch (error) {
          console.error('Error in harvest operation completed callback:', error);
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
    if (!this.connection) {
      this.buildConnection();
    }

    // Start main connection if needed
    if (this.connection) {
      if (this.connection.state === HubConnectionState.Disconnected) {
        try {
          await this.connection.start();
          try {
            const win = window as unknown as { PrintFarmerDebug?: Record<string, unknown> };
            if (win.PrintFarmerDebug?.harvestSignalR) {
              try { console.info('SignalR (harvest) connected'); } catch { /* ignore */ }
            }
          } catch { /* ignore guard errors */ }
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
            try {
              const win = window as unknown as { PrintFarmerDebug?: Record<string, unknown> };
              if (win.PrintFarmerDebug?.harvestSignalR) {
                try { console.info(`Retrying SignalR (harvest) connection in ${delay}ms (attempt ${this.reconnectAttempts})`); } catch { /* ignore */ }
              }
            } catch { /* ignore guard errors */ }
            setTimeout(() => this.connect(), delay);
          } else {
            console.error('Max reconnection attempts reached');
          }
        }
      } else if (this.connection.state === HubConnectionState.Connected) {
        try {
          const win = window as unknown as { PrintFarmerDebug?: Record<string, unknown> };
          if (win.PrintFarmerDebug?.harvestSignalR) {
            try { console.info('SignalR (harvest) already connected'); } catch { /* ignore */ }
          }
        } catch { /* ignore guard errors */ }
      } else if (this.connection.state === HubConnectionState.Connecting) {
        try {
          const win = window as unknown as { PrintFarmerDebug?: Record<string, unknown> };
          if (win.PrintFarmerDebug?.harvestSignalR) {
            try { console.info('SignalR (harvest) already connecting, waiting...'); } catch { /* ignore */ }
          }
        } catch { /* ignore guard errors */ }
      } else {
        console.warn('SignalR (harvest) connection is in unexpected state:', this.connection.state);
      }
    }
  }

  async disconnect(): Promise<void> {
    if (this.connection && this.connection.state === HubConnectionState.Connected) {
      await this.connection.stop();
      try {
        const win = window as unknown as { PrintFarmerDebug?: Record<string, unknown> };
        if (win.PrintFarmerDebug?.harvestSignalR) {
          try { console.info('SignalR disconnected'); } catch { /* ignore */ }
        }
      } catch { /* ignore guard errors */ }
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
    this.harvestOperationProgressCallbacks = [];
    this.harvestOperationCompletedCallbacks = [];
    this.harvestDiscoveryRestartedCallbacks = [];
    this.harvestDiscoveryCompleteCallbacks = [];
    this.jobQueueUpdateCallbacks = [];
    this.connectionStateCallbacks = [];

    if (this.connection) {
      this.connection.stop();
      this.connection = null;
    }
  }
}

// Export singleton instance
export const signalRService = new SignalRService();