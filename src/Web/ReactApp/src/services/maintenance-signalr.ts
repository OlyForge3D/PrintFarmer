// ============================================================================
// Maintenance SignalR Service
// Handles real-time maintenance alert and log updates via SignalR
// ============================================================================

import * as signalR from '@microsoft/signalr';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import type {
  AlertCreatedEvent,
  AlertStatusChangedEvent,
  MaintenanceCompletedEvent
} from '@/types/maintenance';

/**
 * Type definitions for maintenance event handlers
 */
export type AlertCreatedHandler = (event: AlertCreatedEvent) => void;
export type AlertStatusChangedHandler = (event: AlertStatusChangedEvent) => void;
export type MaintenanceCompletedHandler = (event: MaintenanceCompletedEvent) => void;

/**
 * Service for managing SignalR connection to the MaintenanceHub.
 * Provides real-time updates for maintenance alerts, status changes, and completed maintenance.
 */
export class MaintenanceSignalRService {
  private connection: signalR.HubConnection | null = null;
  private readonly hubUrl: string;
  private isConnecting = false;

  // Event handler registries
  private alertCreatedHandlers = new Set<AlertCreatedHandler>();
  private alertStatusChangedHandlers = new Set<AlertStatusChangedHandler>();
  private maintenanceCompletedHandlers = new Set<MaintenanceCompletedHandler>();

  constructor() {
    const baseUrl = getApiBaseUrl();
    this.hubUrl = `${baseUrl}/hubs/maintenance`;
  }

  /**
   * Establishes SignalR connection to the MaintenanceHub.
   * Auto-reconnects on disconnection.
   */
  async start(): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected || this.isConnecting) {
      return;
    }

    try {
      this.isConnecting = true;

      this.connection = new signalR.HubConnectionBuilder()
        .withUrl(this.hubUrl, {
          accessTokenFactory: () => {
            // Get auth token from localStorage (matches apiClient behavior)
            return localStorage.getItem('authToken') || '';
          }
        })
        .withAutomaticReconnect({
          nextRetryDelayInMilliseconds: (retryContext) => {
            // Exponential backoff: 0s, 2s, 10s, 30s, then 30s for subsequent attempts
            const delays = [0, 2000, 10000, 30000];
            return delays[Math.min(retryContext.previousRetryCount, delays.length - 1)];
          }
        })
        .configureLogging(signalR.LogLevel.Information)
        .build();

      // Register event handlers
      this.connection.on('alertcreated', (event: AlertCreatedEvent) => {
        console.log('[MaintenanceSignalR] Alert created:', event);
        this.alertCreatedHandlers.forEach(handler => handler(event));
      });

      this.connection.on('alertstatuschanged', (event: AlertStatusChangedEvent) => {
        console.log('[MaintenanceSignalR] Alert status changed:', event);
        this.alertStatusChangedHandlers.forEach(handler => handler(event));
      });

      this.connection.on('maintenancecompleted', (event: MaintenanceCompletedEvent) => {
        console.log('[MaintenanceSignalR] Maintenance completed:', event);
        this.maintenanceCompletedHandlers.forEach(handler => handler(event));
      });

      // Handle reconnection
      this.connection.onreconnecting(error => {
        console.warn('[MaintenanceSignalR] Reconnecting...', error);
      });

      this.connection.onreconnected(connectionId => {
        console.info('[MaintenanceSignalR] Reconnected. Connection ID:', connectionId);
      });

      this.connection.onclose(error => {
        console.error('[MaintenanceSignalR] Connection closed', error);
        // Attempt to reconnect after a delay
        setTimeout(() => this.start(), 5000);
      });

      await this.connection.start();
      console.info('[MaintenanceSignalR] Connected successfully');
    } catch (error) {
      console.error('[MaintenanceSignalR] Error starting connection:', error);
      // Retry after a delay
      setTimeout(() => this.start(), 5000);
    } finally {
      this.isConnecting = false;
    }
  }

  /**
   * Stops the SignalR connection to the MaintenanceHub.
   */
  async stop(): Promise<void> {
    if (this.connection) {
      try {
        await this.connection.stop();
        console.info('[MaintenanceSignalR] Connection stopped');
      } catch (error) {
        console.error('[MaintenanceSignalR] Error stopping connection:', error);
      }
    }
  }

  /**
   * Gets the current connection state.
   */
  get state(): signalR.HubConnectionState | undefined {
    return this.connection?.state;
  }

  /**
   * Checks if the connection is currently active.
   */
  get isConnected(): boolean {
    return this.connection?.state === signalR.HubConnectionState.Connected;
  }

  // ============================================================================
  // Event Handler Registration
  // ============================================================================

  /**
   * Registers a handler for alert created events.
   * @param handler - Function to call when an alert is created
   * @returns Function to unregister the handler
   */
  onAlertCreated(handler: AlertCreatedHandler): () => void {
    this.alertCreatedHandlers.add(handler);
    return () => this.alertCreatedHandlers.delete(handler);
  }

  /**
   * Registers a handler for alert status changed events.
   * @param handler - Function to call when alert status changes
   * @returns Function to unregister the handler
   */
  onAlertStatusChanged(handler: AlertStatusChangedHandler): () => void {
    this.alertStatusChangedHandlers.add(handler);
    return () => this.alertStatusChangedHandlers.delete(handler);
  }

  /**
   * Registers a handler for maintenance completed events.
   * @param handler - Function to call when maintenance is completed
   * @returns Function to unregister the handler
   */
  onMaintenanceCompleted(handler: MaintenanceCompletedHandler): () => void {
    this.maintenanceCompletedHandlers.add(handler);
    return () => this.maintenanceCompletedHandlers.delete(handler);
  }

  /**
   * Unregisters a specific alert created handler.
   */
  offAlertCreated(handler: AlertCreatedHandler): void {
    this.alertCreatedHandlers.delete(handler);
  }

  /**
   * Unregisters a specific alert status changed handler.
   */
  offAlertStatusChanged(handler: AlertStatusChangedHandler): void {
    this.alertStatusChangedHandlers.delete(handler);
  }

  /**
   * Unregisters a specific maintenance completed handler.
   */
  offMaintenanceCompleted(handler: MaintenanceCompletedHandler): void {
    this.maintenanceCompletedHandlers.delete(handler);
  }

  /**
   * Clears all registered event handlers.
   */
  clearAllHandlers(): void {
    this.alertCreatedHandlers.clear();
    this.alertStatusChangedHandlers.clear();
    this.maintenanceCompletedHandlers.clear();
  }
}

// Export singleton instance
export const maintenanceSignalRService = new MaintenanceSignalRService();
