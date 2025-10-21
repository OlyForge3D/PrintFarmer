import * as signalR from '@microsoft/signalr';

/**
 * SignalR hub events broadcast by the backend
 */
export interface SlicerRegisteredEvent {
  id: string;
  name: string;
  slicerType: number;
  version: string;
  capabilities: string[];
}

export interface SlicerHeartbeatEvent {
  id: string;
  status: string;
  freeSlots: number;
  lastSeen: string;
}

export interface SlicerDeregisteredEvent {
  id: string;
  name: string;
}

export interface SlicerApiKeyRotatedEvent {
  id: string;
  name: string;
  rotatedAt: string;
}

/**
 * Service for managing SignalR connection to the SlicerHub
 * Provides real-time updates for worker status changes
 */
export class SlicerHubService {
  private connection: signalR.HubConnection | null = null;
  private reconnectAttempts = 0;
  private maxReconnectAttempts = 5;
  private reconnectDelay = 5000; // 5 seconds

  /**
   * Start the SignalR connection to the SlicerHub
   */
  async start(baseUrl: string = ''): Promise<void> {
    if (this.connection) {
      console.warn('SlicerHub connection already exists');
      return;
    }

    const hubUrl = `${baseUrl}/hubs/slicers`;
    
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        withCredentials: true,
        skipNegotiation: false,
        transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.ServerSentEvents | signalR.HttpTransportType.LongPolling
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          if (retryContext.previousRetryCount >= this.maxReconnectAttempts) {
            return null; // Stop reconnecting
          }
          return Math.min(1000 * Math.pow(2, retryContext.previousRetryCount), 30000);
        }
      })
      .configureLogging(signalR.LogLevel.Information)
      .build();

    this.connection.onclose((error) => {
      console.log('SlicerHub connection closed', error);
      if (error && this.reconnectAttempts < this.maxReconnectAttempts) {
        setTimeout(() => this.reconnect(), this.reconnectDelay);
      }
    });

    this.connection.onreconnecting((error) => {
      console.log('SlicerHub reconnecting...', error);
    });

    this.connection.onreconnected((connectionId) => {
      console.log('SlicerHub reconnected:', connectionId);
      this.reconnectAttempts = 0;
    });

    try {
      await this.connection.start();
      console.log('SlicerHub connected successfully');
      this.reconnectAttempts = 0;
    } catch (error) {
      console.error('Failed to connect to SlicerHub:', error);
      if (this.reconnectAttempts < this.maxReconnectAttempts) {
        this.reconnectAttempts++;
        setTimeout(() => this.reconnect(), this.reconnectDelay);
      }
    }
  }

  /**
   * Stop the SignalR connection
   */
  async stop(): Promise<void> {
    if (this.connection) {
      await this.connection.stop();
      this.connection = null;
      this.reconnectAttempts = 0;
    }
  }

  /**
   * Attempt to reconnect to the hub
   */
  private async reconnect(): Promise<void> {
    if (this.connection) {
      try {
        await this.connection.start();
        console.log('SlicerHub reconnected successfully');
        this.reconnectAttempts = 0;
      } catch (error) {
        console.error('Failed to reconnect to SlicerHub:', error);
        if (this.reconnectAttempts < this.maxReconnectAttempts) {
          this.reconnectAttempts++;
          setTimeout(() => this.reconnect(), this.reconnectDelay);
        }
      }
    }
  }

  /**
   * Subscribe to worker registered events
   */
  onSlicerRegistered(callback: (event: SlicerRegisteredEvent) => void): void {
    this.connection?.on('SlicerRegistered', callback);
  }

  /**
   * Subscribe to worker heartbeat events
   */
  onSlicerHeartbeat(callback: (event: SlicerHeartbeatEvent) => void): void {
    this.connection?.on('SlicerHeartbeat', callback);
  }

  /**
   * Subscribe to worker deregistered events
   */
  onSlicerDeregistered(callback: (event: SlicerDeregisteredEvent) => void): void {
    this.connection?.on('SlicerDeregistered', callback);
  }

  /**
   * Subscribe to API key rotation events
   */
  onSlicerApiKeyRotated(callback: (event: SlicerApiKeyRotatedEvent) => void): void {
    this.connection?.on('SlicerApiKeyRotated', callback);
  }

  /**
   * Request current registry update
   */
  async requestRegistryUpdate(): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('RequestRegistryUpdateAsync');
    }
  }

  /**
   * Get current connection state
   */
  getConnectionState(): signalR.HubConnectionState {
    return this.connection?.state ?? signalR.HubConnectionState.Disconnected;
  }

  /**
   * Check if connected
   */
  isConnected(): boolean {
    return this.connection?.state === signalR.HubConnectionState.Connected;
  }

  /**
   * Remove event listener
   */
  off(eventName: string, callback?: (...args: unknown[]) => void): void {
    if (callback) {
      this.connection?.off(eventName, callback);
    } else {
      this.connection?.off(eventName);
    }
  }
}

// Singleton instance
export const slicerHubService = new SlicerHubService();
