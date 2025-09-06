import { 
  HubConnection, 
  HubConnectionBuilder, 
  HubConnectionState,
  LogLevel 
} from '@microsoft/signalr';
import { PrinterStatusUpdate, DiscoveryProgressDto, DiscoveryPrinterFoundDto, DiscoveryCompletedDto } from '@/types/api';

type PrinterStatusCallback = (status: PrinterStatusUpdate) => void;
type HarvestUpdateCallback = (operationId: string, status: any) => void;
type JobQueueUpdateCallback = (update: any) => void;
type ConnectionStateCallback = (connected: boolean) => void;
type DiscoveryProgressCallback = (progress: DiscoveryProgressDto) => void;
type DiscoveryPrinterFoundCallback = (found: DiscoveryPrinterFoundDto) => void;
type DiscoveryCompletedCallback = (completed: DiscoveryCompletedDto) => void;

export class SignalRService {
  private connection: HubConnection | null = null;
  private reconnectAttempts = 0;
  private maxReconnectAttempts = 5;
  private reconnectDelay = 1000; // Start with 1 second
  private maxReconnectDelay = 30000; // Max 30 seconds

  // Event handlers
  private printerStatusCallbacks: PrinterStatusCallback[] = [];
  private harvestUpdateCallbacks: HarvestUpdateCallback[] = [];
  private jobQueueUpdateCallbacks: JobQueueUpdateCallback[] = [];
  private connectionStateCallbacks: ConnectionStateCallback[] = [];
  private discoveryProgressCallbacks: DiscoveryProgressCallback[] = [];
  private discoveryPrinterFoundCallbacks: DiscoveryPrinterFoundCallback[] = [];
  private discoveryCompletedCallbacks: DiscoveryCompletedCallback[] = [];

  constructor() {
    this.buildConnection();
  }

  private buildConnection(): void {
    // Use environment variable for SignalR URL, fallback to relative path for monolithic deployment
    const signalrUrl = import.meta.env.VITE_SIGNALR_URL || '/hubs/printers';
    
    this.connection = new HubConnectionBuilder()
      .withUrl(signalrUrl)
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          // Exponential backoff with jitter
          const delay = Math.min(
            this.reconnectDelay * Math.pow(2, retryContext.previousRetryCount),
            this.maxReconnectDelay
          );
          // Add jitter (±10%)
          const jitter = delay * 0.1 * (Math.random() - 0.5);
          return Math.max(1000, delay + jitter);
        }
      })
      .configureLogging(LogLevel.Information)
      .build();

    // Set up event handlers
    this.setupEventHandlers();
  }

  private setupEventHandlers(): void {
    if (!this.connection) return;

    // Connection lifecycle events
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

    // Business event handlers
    this.connection.on('PrinterUpdated', (status: PrinterStatusUpdate) => {
      console.log('SignalR PrinterUpdated received:', status);
      this.printerStatusCallbacks.forEach(callback => {
        try {
          callback(status);
        } catch (error) {
          console.error('Error in printer status callback:', error);
        }
      });
    });

    this.connection.on('HarvestUpdate', (operationId: string, status: any) => {
      this.harvestUpdateCallbacks.forEach(callback => {
        try {
          callback(operationId, status);
        } catch (error) {
          console.error('Error in harvest update callback:', error);
        }
      });
    });

    this.connection.on('JobQueueUpdate', (update: any) => {
      this.jobQueueUpdateCallbacks.forEach(callback => {
        try {
          callback(update);
        } catch (error) {
          console.error('Error in job queue update callback:', error);
        }
      });
    });

    // Discovery event handlers
    this.connection.on('DiscoveryProgress', (progress: DiscoveryProgressDto) => {
      this.discoveryProgressCallbacks.forEach(callback => {
        try {
          callback(progress);
        } catch (error) {
          console.error('Error in discovery progress callback:', error);
        }
      });
    });

    this.connection.on('DiscoveryPrinterFound', (found: DiscoveryPrinterFoundDto) => {
      this.discoveryPrinterFoundCallbacks.forEach(callback => {
        try {
          callback(found);
        } catch (error) {
          console.error('Error in discovery printer found callback:', error);
        }
      });
    });

    this.connection.on('DiscoveryCompleted', (completed: DiscoveryCompletedDto) => {
      this.discoveryCompletedCallbacks.forEach(callback => {
        try {
          callback(completed);
        } catch (error) {
          console.error('Error in discovery completed callback:', error);
        }
      });
    });
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

    if (this.connection!.state === HubConnectionState.Connected) {
      console.info('SignalR already connected');
      return;
    }

    if (this.connection!.state === HubConnectionState.Connecting) {
      console.info('SignalR already connecting, waiting...');
      return;
    }

    // Only start if we're in Disconnected state
    if (this.connection!.state !== HubConnectionState.Disconnected) {
      console.warn('SignalR connection is in unexpected state:', this.connection!.state);
      return;
    }

    try {
      await this.connection!.start();
      console.info('SignalR connected');
      this.reconnectAttempts = 0;
      this.notifyConnectionState(true);
    } catch (error) {
      console.error('SignalR connection failed:', error);
      this.notifyConnectionState(false);
      
      // Retry with exponential backoff
      if (this.reconnectAttempts < this.maxReconnectAttempts) {
        const delay = Math.min(
          this.reconnectDelay * Math.pow(2, this.reconnectAttempts),
          this.maxReconnectDelay
        );
        this.reconnectAttempts++;
        
        console.info(`Retrying SignalR connection in ${delay}ms (attempt ${this.reconnectAttempts})`);
        setTimeout(() => this.connect(), delay);
      } else {
        console.error('Max reconnection attempts reached');
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

  // ============ Discovery Group Methods ============

  async joinDiscoveryGroup(sessionId: string): Promise<void> {
    if (this.connection && this.connection.state === HubConnectionState.Connected) {
      await this.connection.invoke('JoinDiscoveryGroupAsync', sessionId);
    }
  }

  async leaveDiscoveryGroup(sessionId: string): Promise<void> {
    if (this.connection && this.connection.state === HubConnectionState.Connected) {
      await this.connection.invoke('LeaveDiscoveryGroupAsync', sessionId);
    }
  }

  // Clean up all resources
  dispose(): void {
    this.printerStatusCallbacks = [];
    this.harvestUpdateCallbacks = [];
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