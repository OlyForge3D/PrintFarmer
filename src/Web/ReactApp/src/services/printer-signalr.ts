import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import {
  PrinterStatusUpdate,
  JobQueueUpdateDto,
  DiscoveryProgressDto,
  DiscoveryPrinterFoundDto,
  DiscoveryCompletedDto,
} from "@/types/api";
import { apiClient } from "@/services/api";
import { getHubUrl } from "@/common/utils/apiUrlHelpers";

type PrinterStatusCallback = (status: PrinterStatusUpdate) => void;
type JobQueueUpdateCallback = (update: JobQueueUpdateDto) => void;
type ConnectionStateCallback = (connected: boolean) => void;
type DiscoveryProgressCallback = (progress: DiscoveryProgressDto) => void;
type DiscoveryPrinterFoundCallback = (found: DiscoveryPrinterFoundDto) => void;
type DiscoveryCompletedCallback = (completed: DiscoveryCompletedDto) => void;
type PrinterImportProgressCallback = (progress: unknown) => void;

export class PrinterSignalRService {
  // Keep a local cache of last statuses for debugging
  private lastStatuses: Map<string, PrinterStatusUpdate> = new Map();

  public getLastStatus(printerId: string): PrinterStatusUpdate | undefined {
    return this.lastStatuses.get(printerId);
  }

  public getLastStatuses(): Map<string, PrinterStatusUpdate> {
    // Return a defensive copy so callers can't mutate internal state.
    return new Map(this.lastStatuses);
  }

  private buildConnection(): void {
    const printersSignalrUrl = getHubUrl("/hubs/printers");
    // Only emit noisy connection debug when the developer debug flag is enabled
    if (
      (window as unknown as { PrintFarmerDebug?: Record<string, unknown> })
        .PrintFarmerDebug?.printerSignalR
    ) {
      console.info(
        "[printerSignalR] Building connection with URL:",
        printersSignalrUrl
      );
    }
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
        },
      })
      .configureLogging({
        log: (logLevel: number, message: string) => {
          // Suppress benign SignalR warnings about unregistered client methods
          // This happens during initialization before all handlers are attached
          // or when the server sends messages the client hasn't registered yet
          if (message?.includes("No client method with the name")) {
            return;
          }
          if (this.signalrSettings?.consoleLoggingEnabled) {
            const logLevelName =
              [
                "Trace",
                "Debug",
                "Information",
                "Warning",
                "Error",
                "Critical",
                "None",
              ][logLevel] || "Unknown";
            if (typeof window !== 'undefined' && window.PrintFarmerDebug?.printerSignalR) {
              console.log(`[SignalR ${logLevelName}] ${message}`);
            }
          }
        },
      })
      .build();
    
    // Suppress benign UnifiedLoggingService warnings about missing client methods
    // These occur when the server broadcasts messages before all client handlers finish registering,
    // or when a client method hasn't been registered yet on this connection.
    // We use a wrapper that intercepts console.warn to filter these specific messages.
    const originalWarn = console.warn.bind(console);
    console.warn = (...args: Parameters<typeof console.warn>) => {
      const messageStr = String(args?.[0] ?? '');
      // Only suppress warnings about missing client methods; let all other warnings through
      if (messageStr.includes('No client method with the name')) {
        return; // Silently suppress this known harmless warning
      }
      originalWarn(...args);
    };
    
    this.setupEventHandlers();
  }

  private setupEventHandlers(): void {
    if (!this.connection) return;

    // Handler for printerupdated event
    const handlePrinterUpdated = (status: PrinterStatusUpdate) => {
      try {
        const win = window as unknown as {
          PrintFarmerDebug?: Record<string, unknown>;
        };
        if (win.PrintFarmerDebug?.printerSignalR) {
          try {
            console.debug("[printerSignalR] Received printerupdated", {
              id: status.id,
              state: status.state,
              isOnline: status.isOnline,
            });
          } catch {
            /* ignore debug stringify errors */
          }
        }
      } catch {
        // ignore debug guard failures
      }
      // Cache the last status (best-effort)
      try {
        this.lastStatuses.set(status.id, status);
      } catch {
        // ignore cache failures
      }
      // Expose on window for quick inspection (best-effort)
      try {
        const win = window as unknown as {
          PrintFarmerDebug?: Record<string, unknown>;
        };
        if (!win.PrintFarmerDebug) win.PrintFarmerDebug = {};
        win.PrintFarmerDebug.lastPrinterUpdate = status as unknown as Record<
          string,
          unknown
        >;
        win.PrintFarmerDebug.printerSignalR = {
          connectionId: this.connectionId,
          isConnected: this.isConnected,
          lastStatuses: Array.from(this.lastStatuses.entries()).reduce(
            (acc, [k, v]) => {
              acc[k] = v;
              return acc;
            },
            {} as Record<string, unknown>
          ),
        };
      } catch {
        // ignore debug exposure failures
      }

      this.printerStatusCallbacks.forEach((cb) => {
        try {
          cb(status);
        } catch (e) {
          console.error("Printer status cb error:", e);
        }
      });
    };

    // Register single lowercase event name
    this.connection.on("printerupdated", handlePrinterUpdated);

    this.connection.on("jobqueueupdate", (update: JobQueueUpdateDto) => {
      this.jobQueueUpdateCallbacks.forEach((cb) => {
        try {
          cb(update);
        } catch (e) {
          console.error("Job queue cb error:", e);
        }
      });
    });
    // Discovery events - register only lowercase names for consistency
    const handleDiscoveryProgress = (progress: DiscoveryProgressDto) => {
      if (typeof window !== 'undefined' && window.PrintFarmerDebug?.printerSignalR) {
        console.log("[printerSignalR] DiscoveryProgress event received", progress);
      }
      this.discoveryProgressCallbacks.forEach((cb) => {
        try {
          cb(progress);
        } catch (e) {
          console.error("Discovery progress cb error:", e);
        }
      });
    };
    const handleDiscoveryPrinterFound = (found: DiscoveryPrinterFoundDto) => {
      if (typeof window !== 'undefined' && window.PrintFarmerDebug?.printerSignalR) {
        console.log("[printerSignalR] DiscoveryPrinterFound event received", found);
      }
      this.discoveryPrinterFoundCallbacks.forEach((cb) => {
        try {
          cb(found);
        } catch (e) {
          console.error("Discovery printer found cb error:", e);
        }
      });
    };
    const handleDiscoveryCompleted = (completed: DiscoveryCompletedDto) => {
      if (typeof window !== 'undefined' && window.PrintFarmerDebug?.printerSignalR) {
        console.log("[printerSignalR] DiscoveryCompleted event received", completed);
      }
      this.discoveryCompletedCallbacks.forEach((cb) => {
        try {
          cb(completed);
        } catch (e) {
          console.error("Discovery completed cb error:", e);
        }
      });
    };

    // Register only lowercase event names
    this.connection.on("discoveryprogress", handleDiscoveryProgress);
    this.connection.on("discoveryprinterfound", handleDiscoveryPrinterFound);
    this.connection.on("discoverycompleted", handleDiscoveryCompleted);

    // Handler for printer import progress event
    this.connection.on("printerimportprogress", (progress: unknown) => {
      this.printerImportProgressCallbacks.forEach((cb) => {
        try {
          cb(progress);
        } catch (e) {
          console.error("Printer import progress cb error:", e);
        }
      });
    });

    this.connection.onclose(() => this.notifyConnectionState(false));
    this.connection.onreconnecting(() => this.notifyConnectionState(false));
    this.connection.onreconnected(() => {
      this.reconnectAttempts = 0;
      this.notifyConnectionState(true);
    });
    // Add debug hooks for connection lifecycle (gated behind debug flag)
    if (
      (window as unknown as { PrintFarmerDebug?: Record<string, unknown> })
        .PrintFarmerDebug?.printerSignalR
    ) {
      this.connection.onclose((err) =>
        console.info("[printerSignalR] connection closed", err)
      );
      this.connection.onreconnecting((err) =>
        console.info("[printerSignalR] reconnecting", err)
      );
      this.connection.onreconnected((id) =>
        console.info("[printerSignalR] reconnected, connectionId=", id)
      );
    }
  }
  private connection: HubConnection | null = null;
  private reconnectAttempts = 0;
  private maxReconnectAttempts = 5;
  private reconnectDelay = 1000;
  private maxReconnectDelay = 30000;
  private signalrSettings: {
    logLevel: string;
    consoleLoggingEnabled: boolean;
  } | null = null;

  private printerStatusCallbacks: PrinterStatusCallback[] = [];
  private jobQueueUpdateCallbacks: JobQueueUpdateCallback[] = [];
  private connectionStateCallbacks: ConnectionStateCallback[] = [];
  private discoveryProgressCallbacks: DiscoveryProgressCallback[] = [];
  private discoveryPrinterFoundCallbacks: DiscoveryPrinterFoundCallback[] = [];
  private discoveryCompletedCallbacks: DiscoveryCompletedCallback[] = [];
  private printerImportProgressCallbacks: PrinterImportProgressCallback[] = [];

  constructor() {
    this.loadSettings().then(() => {
      this.buildConnection();
    });
  }

  private async loadSettings(): Promise<void> {
    try {
      // UnifiedSettingsController exposes /api/settings/{keyName}
      this.signalrSettings = await apiClient.getSettings<{
        logLevel: string;
        consoleLoggingEnabled: boolean;
      }>("SignalR"); // calls /api/settings/SignalR
    } catch (error) {
      console.warn("Failed to load SignalR settings, using defaults:", error);
      this.signalrSettings = {
        logLevel: "Information",
        consoleLoggingEnabled: true,
      };
    }
  }

  private getLogLevel(): LogLevel {
    if (!this.signalrSettings?.consoleLoggingEnabled) {
      return LogLevel.None;
    }
    switch (this.signalrSettings.logLevel?.toLowerCase()) {
      case "critical":
        return LogLevel.Critical;
      case "error":
        return LogLevel.Error;
      case "warning":
        return LogLevel.Warning;
      case "information":
        return LogLevel.Information;
      case "debug":
        return LogLevel.Debug;
      case "trace":
        return LogLevel.Trace;
      case "none":
        return LogLevel.None;
      default:
        return LogLevel.Information;
    }
  }

  private notifyConnectionState(connected: boolean): void {
    this.connectionStateCallbacks.forEach((cb) => {
      try {
        cb(connected);
      } catch (e) {
        console.error("Connection state cb error:", e);
      }
    });
  }

  public async connect(): Promise<void> {
    if (!this.connection) this.buildConnection();
    if (this.connection!.state === HubConnectionState.Connected) return;
    if (this.connection!.state === HubConnectionState.Connecting) return;
    if (this.connection!.state !== HubConnectionState.Disconnected) return;
    try {
      if (
        (window as unknown as { PrintFarmerDebug?: Record<string, unknown> })
          .PrintFarmerDebug?.printerSignalR
      ) {
        console.info("[printerSignalR] starting connection");
      }
      await this.connection!.start();
      if (
        (window as unknown as { PrintFarmerDebug?: Record<string, unknown> })
          .PrintFarmerDebug?.printerSignalR
      ) {
        console.info("[printerSignalR] connected");
      }
      this.reconnectAttempts = 0;
      this.notifyConnectionState(true);
    } catch {
      console.error("[printerSignalR] connect failed");
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

  // Request the current status for a specific printer from the server
  public async requestPrinterStatus(printerId: string): Promise<void> {
    if (!this.connection) {
      this.buildConnection();
    }
    try {
      if (
        this.connection &&
        this.connection.state === HubConnectionState.Connected
      ) {
        await this.connection.invoke("RequestPrinterStatus", printerId);
      } else {
        // try to connect then invoke
        await this.connect();
        if (
          this.connection &&
          this.connection.state === HubConnectionState.Connected
        ) {
          await this.connection.invoke("RequestPrinterStatus", printerId);
        }
      }
    } catch (err) {
      console.warn("[printerSignalR] requestPrinterStatus failed", err);
      throw err;
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
  public onDiscoveryPrinterFound(
    callback: DiscoveryPrinterFoundCallback
  ): () => void {
    this.discoveryPrinterFoundCallbacks.push(callback);
    return () => {
      const idx = this.discoveryPrinterFoundCallbacks.indexOf(callback);
      if (idx > -1) this.discoveryPrinterFoundCallbacks.splice(idx, 1);
    };
  }
  public onDiscoveryCompleted(
    callback: DiscoveryCompletedCallback
  ): () => void {
    this.discoveryCompletedCallbacks.push(callback);
    return () => {
      const idx = this.discoveryCompletedCallbacks.indexOf(callback);
      if (idx > -1) this.discoveryCompletedCallbacks.splice(idx, 1);
    };
  }

  // Discovery group methods
  public async joinDiscoveryGroup(sessionId: string): Promise<void> {
    if (
      this.connection &&
      this.connection.state === HubConnectionState.Connected
    ) {
      try {
        const win = window as unknown as {
          PrintFarmerDebug?: Record<string, unknown>;
        };
        if (win.PrintFarmerDebug?.discovery) {
          console.debug(
            "[printerSignalR] joinDiscoveryGroup invoked",
            sessionId
          );
        }
      } catch {
        /* ignore */
      }
      await this.connection.invoke("JoinDiscoveryGroupAsync", sessionId);
    }
  }
  public async leaveDiscoveryGroup(sessionId: string): Promise<void> {
    if (
      this.connection &&
      this.connection.state === HubConnectionState.Connected
    ) {
      try {
        const win = window as unknown as {
          PrintFarmerDebug?: Record<string, unknown>;
        };
        if (win.PrintFarmerDebug?.discovery) {
          console.debug(
            "[printerSignalR] leaveDiscoveryGroup invoked",
            sessionId
          );
        }
      } catch {
        /* ignore */
      }
      await this.connection.invoke("LeaveDiscoveryGroupAsync", sessionId);
    }
  }

  public async disconnect(): Promise<void> {
    if (
      this.connection &&
      this.connection.state === HubConnectionState.Connected
    ) {
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

  onPrinterImportProgress(callback: PrinterImportProgressCallback): () => void {
    this.printerImportProgressCallbacks.push(callback);
    return () => {
      const idx = this.printerImportProgressCallbacks.indexOf(callback);
      if (idx > -1) this.printerImportProgressCallbacks.splice(idx, 1);
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
    this.printerImportProgressCallbacks = [];
    if (this.connection) {
      this.connection.stop();
      this.connection = null;
    }
  }
}

export const printerSignalRService = new PrinterSignalRService();

// Debug helper: get a snapshot of last known statuses (populated by the service)
export function getPrinterSignalRDebug(): {
  connectionId: string | null;
  isConnected: boolean;
  lastStatuses: Record<string, unknown>;
} {
  // Attempt to read cached map via the instance (internal). If not available, return basic info.
  try {
    const svc = printerSignalRService as unknown as {
      lastStatuses?: Map<string, unknown>;
    };
    const map: Map<string, unknown> = svc.lastStatuses || new Map();
    return {
      connectionId: printerSignalRService.connectionId,
      isConnected: printerSignalRService.isConnected,
      lastStatuses: Array.from(map.entries()).reduce((acc, [k, v]) => {
        acc[k] = v;
        return acc;
      }, {} as Record<string, unknown>),
    };
  } catch {
    return {
      connectionId: printerSignalRService.connectionId,
      isConnected: printerSignalRService.isConnected,
      lastStatuses: {},
    };
  }
}

// Expose a convenience function on window for interactive debugging
try {
  const win = window as unknown as {
    PrintFarmerDebug?: Record<string, unknown>;
  };
  if (win.PrintFarmerDebug) {
    win.PrintFarmerDebug.requestPrinterStatus = async (printerId: string) => {
      try {
        await printerSignalRService.connect();
        return await printerSignalRService.requestPrinterStatus(printerId);
      } catch (err) {
        console.error("requestPrinterStatus failed", err);
        throw err;
      }
    };
  }
} catch {
  // ignore exposing debug helper
}

// Additional interactive helpers for discovery debugging (gated by PrintFarmerDebug.discovery)
try {
  const win = window as unknown as {
    PrintFarmerDebug?: Record<string, unknown>;
  };
  if (!win.PrintFarmerDebug) win.PrintFarmerDebug = {};
  // Only expose these helpers when the discovery gate is enabled to avoid accidental use in production
  win.PrintFarmerDebug.joinDiscoveryGroup = async (sessionId: string) => {
    if (
      !win.PrintFarmerDebug ||
      !("discovery" in win.PrintFarmerDebug) ||
      !win.PrintFarmerDebug.discovery
    ) {
      console.warn(
        "Enable discovery debug first: window.PrintFarmerDebug.discovery = true"
      );
      return;
    }
    try {
      await printerSignalRService.connect();
      if (window.PrintFarmerDebug?.discovery) {
        console.debug(
          "[printerSignalR.debug] manual joinDiscoveryGroup",
          sessionId
        );
      }
      await printerSignalRService.joinDiscoveryGroup(sessionId);
      if (window.PrintFarmerDebug?.discovery) {
        console.debug(
          "[printerSignalR.debug] joinDiscoveryGroup complete",
          sessionId
        );
      }
    } catch (err) {
      console.error("[printerSignalR.debug] joinDiscoveryGroup failed", err);
      throw err;
    }
  };

  win.PrintFarmerDebug.leaveDiscoveryGroup = async (sessionId: string) => {
    if (
      !win.PrintFarmerDebug ||
      !("discovery" in win.PrintFarmerDebug) ||
      !win.PrintFarmerDebug.discovery
    ) {
      console.warn(
        "Enable discovery debug first: window.PrintFarmerDebug.discovery = true"
      );
      return;
    }
    try {
      if (window.PrintFarmerDebug?.discovery) {
        console.debug(
          "[printerSignalR.debug] manual leaveDiscoveryGroup",
          sessionId
        );
      }
      await printerSignalRService.leaveDiscoveryGroup(sessionId);
      if (window.PrintFarmerDebug?.discovery) {
        console.debug(
          "[printerSignalR.debug] leaveDiscoveryGroup complete",
          sessionId
        );
      }
    } catch (err) {
      console.error("[printerSignalR.debug] leaveDiscoveryGroup failed", err);
      throw err;
    }
  };

  win.PrintFarmerDebug.getSignalRDebug = () => {
    return getPrinterSignalRDebug();
  };
  // Allow attaching runtime discovery event handlers from the console
  win.PrintFarmerDebug.onDiscoveryProgress = (
    cb: (p: DiscoveryProgressDto) => void
  ) => {
    try {
      return printerSignalRService.onDiscoveryProgress(cb);
    } catch (err) {
      console.error(
        "[printerSignalR.debug] onDiscoveryProgress attach failed",
        err
      );
      return () => {
        /* noop */
      };
    }
  };
  win.PrintFarmerDebug.onDiscoveryPrinterFound = (
    cb: (f: DiscoveryPrinterFoundDto) => void
  ) => {
    try {
      return printerSignalRService.onDiscoveryPrinterFound(cb);
    } catch (err) {
      console.error(
        "[printerSignalR.debug] onDiscoveryPrinterFound attach failed",
        err
      );
      return () => {
        /* noop */
      };
    }
  };
  win.PrintFarmerDebug.onDiscoveryCompleted = (
    cb: (c: DiscoveryCompletedDto) => void
  ) => {
    try {
      return printerSignalRService.onDiscoveryCompleted(cb);
    } catch (err) {
      console.error(
        "[printerSignalR.debug] onDiscoveryCompleted attach failed",
        err
      );
      return () => {
        /* noop */
      };
    }
  };
} catch {
  // ignore
}
