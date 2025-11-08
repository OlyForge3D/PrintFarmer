import * as signalR from "@microsoft/signalr";

/**
 * SignalR hub events broadcast by the backend
 */
export interface PrinterImportProgress {
  index: number;
  name: string;
  status: "Pending" | "Imported" | "Skipped" | "Failed";
  id?: string;
  reason?: string;
}

/**
 * Service for managing SignalR connection to the PrinterHub
 * Provides real-time updates for printer imports and discovery
 */
export class PrinterHubService {
  private connection: signalR.HubConnection | null = null;
  private reconnectAttempts = 0;
  private maxReconnectAttempts = 5;
  private reconnectDelay = 5000; // 5 seconds

  /**
   * Start the SignalR connection to the PrinterHub
   */
  async start(baseUrl: string = ""): Promise<void> {
    if (this.connection) {
      console.warn("PrinterHub connection already exists");
      return;
    }

    const hubUrl = `${baseUrl}/hubs/printers`;

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        withCredentials: true,
        skipNegotiation: false,
        transport:
          signalR.HttpTransportType.WebSockets |
          signalR.HttpTransportType.ServerSentEvents |
          signalR.HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          if (retryContext.previousRetryCount >= this.maxReconnectAttempts) {
            return null; // Stop reconnecting
          }
          return Math.min(
            1000 * Math.pow(2, retryContext.previousRetryCount),
            30000
          );
        },
      })
      .configureLogging(signalR.LogLevel.Information)
      .build();

    this.connection.onclose((error) => {
      console.log("PrinterHub connection closed", error);
      if (error && this.reconnectAttempts < this.maxReconnectAttempts) {
        setTimeout(() => this.reconnect(), this.reconnectDelay);
      }
    });

    this.connection.onreconnecting((error) => {
      console.log("PrinterHub reconnecting...", error);
    });

    this.connection.onreconnected((connectionId) => {
      console.log("PrinterHub reconnected:", connectionId);
      this.reconnectAttempts = 0;
    });

    try {
      await this.connection.start();
      console.log("PrinterHub connected successfully");
      this.reconnectAttempts = 0;
    } catch (error) {
      console.error("Failed to connect to PrinterHub:", error);
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
      try {
        await this.connection.stop();
        this.connection = null;
        console.log("PrinterHub connection stopped");
      } catch (error) {
        console.error("Error stopping PrinterHub connection:", error);
      }
    }
  }

  /**
   * Attempt to reconnect
   */
  private async reconnect(): Promise<void> {
    if (
      this.connection &&
      this.connection.state === signalR.HubConnectionState.Disconnected
    ) {
      try {
        await this.connection.start();
        console.log("PrinterHub reconnected");
        this.reconnectAttempts = 0;
      } catch (error) {
        console.error("Reconnection failed:", error);
        if (this.reconnectAttempts < this.maxReconnectAttempts) {
          this.reconnectAttempts++;
          setTimeout(() => this.reconnect(), this.reconnectDelay);
        }
      }
    }
  }

  /**
   * Subscribe to printer import progress updates
   */
  onPrinterImportProgress(
    callback: (progress: PrinterImportProgress) => void
  ): () => void {
    if (!this.connection) {
      throw new Error(
        "PrinterHub connection not established. Call start() first."
      );
    }

    this.connection.on("printerimportprogress", callback);

    // Return unsubscribe function
    return () => {
      if (this.connection) {
        this.connection.off("printerimportprogress", callback);
      }
    };
  }

  /**
   * Check if connected
   */
  isConnected(): boolean {
    return this.connection?.state === signalR.HubConnectionState.Connected;
  }
}

// Create a singleton instance
export const printerHubService = new PrinterHubService();
