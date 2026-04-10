/* eslint-disable local/pf-no-unguarded-console */
import * as signalR from '@microsoft/signalr';
import { getHubUrl } from '@/common/utils/apiUrlHelpers';

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

export type SliceJobEventType =
  | 'JobQueued'
  | 'JobStarted'
  | 'JobProgress'
  | 'JobCompleted'
  | 'JobFailed'
  | 'JobCancelled';

export interface SliceJobEvent {
  eventType: SliceJobEventType;
  jobId: string;
  userId: string;
  printerId?: string;
  status: string;
  progressPercent?: number;
  progressMessage?: string;
  queuedAt?: string;
  startedAt?: string;
  completedAt?: string;
  resultFileUrl?: string;
  estimatedPrintTimeSeconds?: number;
  filamentUsedGrams?: number;
  errorMessage?: string;
  workerId?: string;
  timestamp: string;
}

/**
 * Service for managing SignalR connection to the SlicerHub.
 * Handles both worker lifecycle events and slice job progress events.
 */
export class SlicerHubService {
  private connection: signalR.HubConnection | null = null;
  private reconnectAttempts = 0;
  private maxReconnectAttempts = 5;
  private reconnectDelay = 5000;
  private connectionPromise: Promise<void> | null = null;
  private reconnectCallbacks = new Set<() => Promise<void>>();

  /**
   * Register a callback to re-establish subscriptions after reconnect.
   * Returns a cleanup function to deregister the callback.
   */
  onReconnected(callback: () => Promise<void>): () => void {
    this.reconnectCallbacks.add(callback);
    return () => { this.reconnectCallbacks.delete(callback); };
  }

  async start(baseUrl: string = ''): Promise<void> {
    if (this.connection) {
      console.warn('SlicerHub connection already exists');
      return;
    }

    const hubUrl = baseUrl ? `${baseUrl}/hubs/slicers` : getHubUrl('/hubs/slicers');

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => localStorage.getItem('authToken') || '',
        withCredentials: true,
        skipNegotiation: false,
        transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.ServerSentEvents | signalR.HttpTransportType.LongPolling
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          if (retryContext.previousRetryCount >= this.maxReconnectAttempts) {
            return null;
          }
          return Math.min(1000 * Math.pow(2, retryContext.previousRetryCount), 30000);
        }
      })
      .configureLogging(signalR.LogLevel.Information)
      .build();

    this.connection.onclose((error) => {
      console.debug('SlicerHub connection closed', error);
      this.connectionPromise = null;
      if (error && this.reconnectAttempts < this.maxReconnectAttempts) {
        setTimeout(() => this.reconnect(), this.reconnectDelay);
      }
    });

    this.connection.onreconnecting((error) => {
      console.debug('SlicerHub reconnecting...', error);
    });

    this.connection.onreconnected(async (connectionId) => {
      console.debug('SlicerHub reconnected:', connectionId);
      this.reconnectAttempts = 0;
      for (const cb of this.reconnectCallbacks) {
        try { await cb(); } catch (e) { console.error('SlicerHub reconnect callback failed:', e); }
      }
    });

    this.connectionPromise = this.connection.start().then(() => {
      console.debug('SlicerHub connected successfully');
      this.reconnectAttempts = 0;
    }).catch((error) => {
      console.error('Failed to connect to SlicerHub:', error);
      this.connection = null;
      this.connectionPromise = null;
      if (this.reconnectAttempts < this.maxReconnectAttempts) {
        this.reconnectAttempts++;
        setTimeout(() => this.reconnect(), this.reconnectDelay);
      }
    });

    return this.connectionPromise;
  }

  async stop(): Promise<void> {
    if (this.connection) {
      await this.connection.stop();
      this.connection = null;
      this.connectionPromise = null;
      this.reconnectAttempts = 0;
      this.reconnectCallbacks.clear();
    }
  }

  private async reconnect(): Promise<void> {
    if (this.connection) {
      try {
        await this.connection.start();
        console.debug('SlicerHub reconnected successfully');
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

  /** Ensure connection is started; returns immediately if already connected */
  async ensureConnected(): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      return;
    }
    if (this.connectionPromise) {
      return this.connectionPromise;
    }
    return this.start();
  }

  // ── Worker lifecycle events ──

  onSlicerRegistered(callback: (event: SlicerRegisteredEvent) => void): void {
    this.connection?.on('SlicerRegistered', callback);
  }

  onSlicerHeartbeat(callback: (event: SlicerHeartbeatEvent) => void): void {
    this.connection?.on('SlicerHeartbeat', callback);
  }

  onSlicerDeregistered(callback: (event: SlicerDeregisteredEvent) => void): void {
    this.connection?.on('SlicerDeregistered', callback);
  }

  onSlicerApiKeyRotated(callback: (event: SlicerApiKeyRotatedEvent) => void): void {
    this.connection?.on('SlicerApiKeyRotated', callback);
  }

  async requestRegistryUpdate(): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('RequestRegistryUpdateAsync');
    }
  }

  // ── Slice job progress events ──

  async subscribeToJob(jobId: string): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('SubscribeToJobAsync', jobId);
    }
  }

  async unsubscribeFromJob(jobId: string): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('UnsubscribeFromJobAsync', jobId);
    }
  }

  async joinUserGroup(userId: string): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('JoinUserGroupAsync', userId);
    }
  }

  async leaveUserGroup(userId: string): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('LeaveUserGroupAsync', userId);
    }
  }

  onJobEvent(jobId: string, callback: (event: SliceJobEvent) => void): () => void {
    const channel = `SliceJob_${jobId}`;
    this.connection?.on(channel, callback);
    return () => { this.connection?.off(channel, callback); };
  }

  onUserJobEvent(callback: (event: SliceJobEvent) => void): () => void {
    this.connection?.on('slicejobevent', callback);
    return () => { this.connection?.off('slicejobevent', callback); };
  }

  // ── Connection state ──

  getConnectionState(): signalR.HubConnectionState {
    return this.connection?.state ?? signalR.HubConnectionState.Disconnected;
  }

  isConnected(): boolean {
    return this.connection?.state === signalR.HubConnectionState.Connected;
  }

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
