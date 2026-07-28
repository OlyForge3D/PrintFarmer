import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import {
  AutoDispatchStatus,
  PrinterStatusUpdate,
  JobQueueUpdateDto,
  DiscoveryProgressDto,
  DiscoveryPrinterFoundDto,
  DiscoveryCompletedDto,
  DispatchUploadProgressDto,
  FailureDetectionEvent,
  QueueEventEnvelope,
} from "@/types/api";
import { apiClient } from "@/services/api";
import { getHubUrl } from "@/common/utils/apiUrlHelpers";
import { AUTH_SESSION_ESTABLISHED_EVENT } from "@/services/authEvents";

type PrinterStatusCallback = (status: PrinterStatusUpdate) => void;
type JobQueueUpdateCallback = (update: JobQueueUpdateDto) => void;
type ConnectionStateCallback = (connected: boolean) => void;
type DiscoveryProgressCallback = (progress: DiscoveryProgressDto) => void;
type DiscoveryPrinterFoundCallback = (found: DiscoveryPrinterFoundDto) => void;
type DiscoveryCompletedCallback = (completed: DiscoveryCompletedDto) => void;
type PrinterImportProgressCallback = (progress: unknown) => void;
type DispatchUploadProgressCallback = (progress: DispatchUploadProgressDto) => void;
type FailureDetectionCallback = (event: FailureDetectionEvent) => void;
type AutoDispatchStatusCallback = (status: AutoDispatchStatus) => void;
type QueueEventCallback = (event: QueueEventEnvelope) => void;
type QueueResourcesChangedCallback = () => void;

const AUTO_DISPATCH_STATE_CHANGED_EVENT = "autodispatchstatechanged";

export class PrinterSignalRService {
  // Keep a local cache of last statuses for debugging
  private lastStatuses: Map<string, PrinterStatusUpdate> = new Map();
  /** Pending offline timers keyed by printer ID — suppresses transient offline flicker */
  private offlineGraceTimers: Map<string, ReturnType<typeof setTimeout>> = new Map();
  /** Grace period (ms) before broadcasting an online→offline transition */
  private static readonly OFFLINE_GRACE_MS = 1_000;

  public getLastStatus(printerId: string): PrinterStatusUpdate | undefined {
    return this.lastStatuses.get(printerId);
  }

  public getLastStatuses(): Map<string, PrinterStatusUpdate> {
    // Return a defensive copy so callers can't mutate internal state.
    return new Map(this.lastStatuses);
  }

  private buildConnection(): void {
    if (this.disposed) return;
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
    const connection = new HubConnectionBuilder()
      .withUrl(printersSignalrUrl, {
        accessTokenFactory: () => localStorage.getItem("auth-token") ?? "",
        withCredentials: true,
      })
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

    this.connection = connection;
    this.setupEventHandlers(connection);
  }

  private setupEventHandlers(connection: HubConnection): void {
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
      // --- Offline debounce logic ---
      // Suppress brief online→offline flickers caused by transient WebSocket/network hiccups.
      // If a printer goes offline, we wait OFFLINE_GRACE_MS before telling the UI.
      // If it comes back online within that window, the offline event is silently discarded.
      const previousStatus = this.lastStatuses.get(status.id);
      const wasOnline = previousStatus?.isOnline !== false; // true or undefined → was online

      if (status.isOnline === false && wasOnline) {
        // Online→Offline transition: start grace period instead of broadcasting immediately
        if (!this.offlineGraceTimers.has(status.id)) {
          const timer = setTimeout(() => {
            this.offlineGraceTimers.delete(status.id);
            // Grace period expired — printer is genuinely offline
            this.applyStatusUpdate(status);
          }, PrinterSignalRService.OFFLINE_GRACE_MS);
          this.offlineGraceTimers.set(status.id, timer);
        }
        return; // Don't broadcast yet
      }

      if (status.isOnline) {
        // Cancel any pending offline grace timer — printer recovered
        const pendingTimer = this.offlineGraceTimers.get(status.id);
        if (pendingTimer) {
          clearTimeout(pendingTimer);
          this.offlineGraceTimers.delete(status.id);
        }
      }

      // Normal flow: cache + broadcast
      this.applyStatusUpdate(status);
    };

    // Register single lowercase event name
    connection.on("printerupdated", handlePrinterUpdated);

    connection.on("jobqueueupdate", (update: JobQueueUpdateDto) => {
      this.jobQueueUpdateCallbacks.forEach((cb) => {
        try {
          cb(update);
        } catch (e) {
          console.error("Job queue cb error:", e);
        }
      });
    });
    connection.on("queueevent", (event: QueueEventEnvelope) => {
      void this.handleQueueEvent(event);
    });
    connection.on("queueresourceschanged", () => {
      this.queueResourcesChangedCallbacks.forEach((callback) => {
        try {
          callback();
        } catch (error) {
          console.error("Queue resource callback error:", error);
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
    connection.on("discoveryprogress", handleDiscoveryProgress);
    connection.on("discoveryprinterfound", handleDiscoveryPrinterFound);
    connection.on("discoverycompleted", handleDiscoveryCompleted);

    // Handler for printer import progress event
    connection.on("printerimportprogress", (progress: unknown) => {
      this.printerImportProgressCallbacks.forEach((cb) => {
        try {
          cb(progress);
        } catch (e) {
          console.error("Printer import progress cb error:", e);
        }
      });
    });

    // Dispatch upload progress event
    connection.on(
      "dispatchuploadprogress",
      (progress: DispatchUploadProgressDto) => {
        this.dispatchUploadProgressCallbacks.forEach((cb) => {
          try {
            cb(progress);
          } catch (e) {
            console.error("Dispatch upload progress cb error:", e);
          }
        });
      }
    );

    // Failure detection event
    connection.on(
      "failuredetected",
      (event: FailureDetectionEvent) => {
        if (typeof window !== 'undefined' && window.PrintFarmerDebug?.printerSignalR) {
          console.log("[printerSignalR] FailureDetected event received", event);
        }
        this.failureDetectionCallbacks.forEach((cb) => {
          try {
            cb(event);
          } catch (e) {
            console.error("Failure detection cb error:", e);
          }
        });
      }
    );

    connection.on(
      AUTO_DISPATCH_STATE_CHANGED_EVENT,
      (status: AutoDispatchStatus) => {
        this.autoDispatchStatusCallbacks.forEach((cb) => {
          try {
            cb(status);
          } catch (e) {
            console.error("Auto-dispatch status callback error:", e);
          }
        });
      }
    );

    connection.onclose(() => {
      if (connection !== this.connection) return;
      this.invalidateConnectionEpoch();
      this.notifyConnectionState(false);
    });
    connection.onreconnecting(() => {
      if (connection !== this.connection) return;
      this.invalidateConnectionEpoch();
      this.notifyConnectionState(false);
      if (this.disposed || !this.connectionRequested) {
        void connection.stop();
      }
    });
    connection.onreconnected(() => {
      if (
        connection !== this.connection ||
        this.disposed ||
        !this.connectionRequested
      ) {
        void connection.stop();
        return;
      }
      this.reconnectAttempts = 0;
      this.clearManualReconnectTimer();
      const connectionEpoch = this.beginConnectionEpoch();
      this.notifyConnectionState(true);
      void this.restoreSubscriptionsAndDrain(connection, connectionEpoch);
    });
    // Add debug hooks for connection lifecycle (gated behind debug flag)
    if (
      (window as unknown as { PrintFarmerDebug?: Record<string, unknown> })
        .PrintFarmerDebug?.printerSignalR
    ) {
      connection.onclose((err) =>
        console.info("[printerSignalR] connection closed", err)
      );
      connection.onreconnecting((err) =>
        console.info("[printerSignalR] reconnecting", err)
      );
      connection.onreconnected((id) =>
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
  private isRefreshingSettings = false;
  private authListener: (() => void) | null = null;

  private printerStatusCallbacks: PrinterStatusCallback[] = [];
  private jobQueueUpdateCallbacks: JobQueueUpdateCallback[] = [];
  private connectionStateCallbacks: ConnectionStateCallback[] = [];
  private discoveryProgressCallbacks: DiscoveryProgressCallback[] = [];
  private discoveryPrinterFoundCallbacks: DiscoveryPrinterFoundCallback[] = [];
  private discoveryCompletedCallbacks: DiscoveryCompletedCallback[] = [];
  private printerImportProgressCallbacks: PrinterImportProgressCallback[] = [];
  private dispatchUploadProgressCallbacks: DispatchUploadProgressCallback[] = [];
  private failureDetectionCallbacks: FailureDetectionCallback[] = [];
  private autoDispatchStatusCallbacks: AutoDispatchStatusCallback[] = [];
  private queueEventCallbacks: QueueEventCallback[] = [];
  private queueResourcesChangedCallbacks: QueueResourcesChangedCallback[] = [];
  private subscribedPrinters = new Set<string>();
  private subscribedQueueJobs = new Set<string>();
  private subscribedProjects = new Set<string>();
  private desiredQueuePrinters = new Set<string>();
  private desiredQueueJobs = new Set<string>();
  private desiredQueueProjects = new Set<string>();
  private queueSubscriptionGeneration = 0;
  private queueSubscriptionTail: Promise<void> = Promise.resolve();
  private disposed = false;
  private connectionRequested = false;
  private connectionIntentGeneration = 0;
  private connectionEpoch = 0;
  private manualReconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private lastQueueSequence = 0;
  private queueDrain: Promise<void> | null = null;

  constructor() {
    this.loadSettings().then(() => {
      if (!this.disposed) this.buildConnection();
    });
    // The initial load above runs at module-import time, before the user has
    // authenticated, so the anonymous GET /api/settings/SignalR fails closed
    // (401) and falls back to defaults. Re-load once a session is established so
    // the admin-configured log level is actually honoured for the session.
    this.authListener = () => {
      void this.refreshSettings();
    };
    window.addEventListener(AUTH_SESSION_ESTABLISHED_EVENT, this.authListener);
    if (window.PrintFarmerDebug?.printerSignalR) {
      window.PrintFarmerDebug.printerSignalRService = this;
    }
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

  /**
   * Re-fetch the SignalR settings section (e.g. after the user authenticates)
   * and, if the effective log level changed, rebuild the connection so the new
   * level takes effect — the level is only applied when the connection is built.
   * Guarded against overlapping runs so it cannot race with itself, and it emits
   * no events, so it cannot re-trigger its own auth listener.
   */
  public async refreshSettings(): Promise<void> {
    if (this.isRefreshingSettings) {
      return;
    }
    this.isRefreshingSettings = true;
    try {
      const previousLevel = this.getLogLevel();
      await this.loadSettings();
      if (this.getLogLevel() === previousLevel) {
        // Nothing changed (or still on defaults) — no reconnect needed.
        return;
      }
      const wasActive =
        this.connection?.state === HubConnectionState.Connected ||
        this.connection?.state === HubConnectionState.Connecting ||
        this.connection?.state === HubConnectionState.Reconnecting;
      const shouldReconnect = this.connectionRequested || wasActive;
      const previousConnection = this.connection;
      this.connectionIntentGeneration++;
      this.clearManualReconnectTimer();
      this.invalidateConnectionEpoch();
      if (
        previousConnection &&
        previousConnection.state !== HubConnectionState.Disconnected
      ) {
        await previousConnection.stop();
      }
      if (previousConnection === this.connection) {
        this.connection = null;
      }
      if (this.disposed) return;
      this.buildConnection();
      if (shouldReconnect) {
        await this.connect();
      }
    } finally {
      this.isRefreshingSettings = false;
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

  /** Apply a status update: cache it, expose for debug, and notify all subscribers. */
  private applyStatusUpdate(status: PrinterStatusUpdate): void {
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
    if (this.disposed) return;
    this.connectionRequested = true;
    this.clearManualReconnectTimer();
    if (!this.connection) this.buildConnection();
    if (this.connection!.state === HubConnectionState.Connected) return;
    if (this.connection!.state === HubConnectionState.Connecting) return;
    if (this.connection!.state === HubConnectionState.Reconnecting) return;
    if (this.connection!.state !== HubConnectionState.Disconnected) return;
    const connection = this.connection!;
    const intentGeneration = ++this.connectionIntentGeneration;
    try {
      if (
        (window as unknown as { PrintFarmerDebug?: Record<string, unknown> })
          .PrintFarmerDebug?.printerSignalR
      ) {
        console.info("[printerSignalR] starting connection");
      }
      await connection.start();
      if (!this.isCurrentConnectionIntent(connection, intentGeneration)) {
        await this.stopConnection(connection);
        return;
      }
      const connectionEpoch = this.beginConnectionEpoch();
      await this.restoreResourceSubscriptions(connection, connectionEpoch);
      if (!this.isCurrentConnectionIntent(connection, intentGeneration)) {
        await this.stopConnection(connection);
        return;
      }
      await this.drainQueueChanges();
      if (!this.isCurrentConnectionIntent(connection, intentGeneration)) {
        await this.stopConnection(connection);
        return;
      }
      if (
        (window as unknown as { PrintFarmerDebug?: Record<string, unknown> })
          .PrintFarmerDebug?.printerSignalR
      ) {
        console.info("[printerSignalR] connected");
      }
      this.reconnectAttempts = 0;
      this.notifyConnectionState(true);
    } catch {
      if (!this.isCurrentConnectionIntent(connection, intentGeneration)) {
        return;
      }
      console.error("[printerSignalR] connect failed");
      this.notifyConnectionState(false);
      if (
        this.connectionRequested &&
        this.reconnectAttempts < this.maxReconnectAttempts
      ) {
        const delay = Math.min(
          this.reconnectDelay * Math.pow(2, this.reconnectAttempts),
          this.maxReconnectDelay
        );
        this.reconnectAttempts++;
        this.manualReconnectTimer = setTimeout(() => {
          this.manualReconnectTimer = null;
          if (this.isCurrentConnectionIntent(connection, intentGeneration)) {
            void this.connect();
          }
        }, delay);
      }
    }
  }

  private isCurrentConnectionIntent(
    connection: HubConnection,
    intentGeneration: number): boolean {
    return (
      !this.disposed &&
      this.connectionRequested &&
      connection === this.connection &&
      intentGeneration === this.connectionIntentGeneration
    );
  }

  private clearManualReconnectTimer(): void {
    if (this.manualReconnectTimer) {
      clearTimeout(this.manualReconnectTimer);
      this.manualReconnectTimer = null;
    }
  }

  private async stopConnection(connection: HubConnection): Promise<void> {
    if (connection.state !== HubConnectionState.Disconnected) {
      await connection.stop();
    }
  }

  private beginConnectionEpoch(): number {
    this.connectionEpoch++;
    this.clearAppliedQueueSubscriptionState();
    return this.connectionEpoch;
  }

  private invalidateConnectionEpoch(): void {
    this.connectionEpoch++;
    this.clearAppliedQueueSubscriptionState();
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
        await this.subscribeToPrinter(printerId);
        await this.connection.invoke("RequestPrinterStatus", printerId);
      } else {
        // try to connect then invoke
        await this.connect();
        if (
          this.connection &&
          this.connection.state === HubConnectionState.Connected
        ) {
          await this.subscribeToPrinter(printerId);
          await this.connection.invoke("RequestPrinterStatus", printerId);
        }
      }
    } catch (err) {
      console.warn("[printerSignalR] requestPrinterStatus failed", err);
      throw err;
    }
  }

  public async subscribeToPrinter(printerId: string): Promise<void> {
    this.desiredQueuePrinters.add(printerId);
    await this.applySubscribeToPrinter(printerId);
  }

  private async applySubscribeToPrinter(printerId: string): Promise<void> {
    const connection = this.connection;
    const connectionEpoch = this.connectionEpoch;
    if (connection?.state === HubConnectionState.Connected) {
      await connection.invoke("SubscribeToPrinterAsync", printerId);
      if (this.isCurrentConnectionEpoch(connection, connectionEpoch)) {
        this.subscribedPrinters.add(printerId);
      }
    }
  }

  public async unsubscribeFromPrinter(printerId: string): Promise<void> {
    this.desiredQueuePrinters.delete(printerId);
    await this.applyUnsubscribeFromPrinter(printerId);
  }

  private async applyUnsubscribeFromPrinter(printerId: string): Promise<void> {
    const connection = this.connection;
    const connectionEpoch = this.connectionEpoch;
    if (connection?.state === HubConnectionState.Connected) {
      await connection.invoke("UnsubscribeFromPrinterAsync", printerId);
      if (this.isCurrentConnectionEpoch(connection, connectionEpoch)) {
        this.subscribedPrinters.delete(printerId);
      }
    } else {
      this.subscribedPrinters.delete(printerId);
    }
  }

  public async subscribeToQueueJob(jobId: string): Promise<void> {
    this.desiredQueueJobs.add(jobId);
    await this.applySubscribeToQueueJob(jobId);
  }

  private async applySubscribeToQueueJob(jobId: string): Promise<void> {
    const connection = this.connection;
    const connectionEpoch = this.connectionEpoch;
    if (connection?.state === HubConnectionState.Connected) {
      await connection.invoke("SubscribeToQueueJobAsync", jobId);
      if (this.isCurrentConnectionEpoch(connection, connectionEpoch)) {
        this.subscribedQueueJobs.add(jobId);
      }
    }
  }

  public async unsubscribeFromQueueJob(jobId: string): Promise<void> {
    this.desiredQueueJobs.delete(jobId);
    await this.applyUnsubscribeFromQueueJob(jobId);
  }

  private async applyUnsubscribeFromQueueJob(jobId: string): Promise<void> {
    const connection = this.connection;
    const connectionEpoch = this.connectionEpoch;
    if (connection?.state === HubConnectionState.Connected) {
      await connection.invoke("UnsubscribeFromQueueJobAsync", jobId);
      if (this.isCurrentConnectionEpoch(connection, connectionEpoch)) {
        this.subscribedQueueJobs.delete(jobId);
      }
    } else {
      this.subscribedQueueJobs.delete(jobId);
    }
  }

  public async subscribeToProject(projectId: string): Promise<void> {
    this.desiredQueueProjects.add(projectId);
    await this.applySubscribeToProject(projectId);
  }

  private async applySubscribeToProject(projectId: string): Promise<void> {
    const connection = this.connection;
    const connectionEpoch = this.connectionEpoch;
    if (connection?.state === HubConnectionState.Connected) {
      await connection.invoke("SubscribeToProjectAsync", projectId);
      if (this.isCurrentConnectionEpoch(connection, connectionEpoch)) {
        this.subscribedProjects.add(projectId);
      }
    }
  }

  public async unsubscribeFromProject(projectId: string): Promise<void> {
    this.desiredQueueProjects.delete(projectId);
    await this.applyUnsubscribeFromProject(projectId);
  }

  private async applyUnsubscribeFromProject(projectId: string): Promise<void> {
    const connection = this.connection;
    const connectionEpoch = this.connectionEpoch;
    if (connection?.state === HubConnectionState.Connected) {
      await connection.invoke("UnsubscribeFromProjectAsync", projectId);
      if (this.isCurrentConnectionEpoch(connection, connectionEpoch)) {
        this.subscribedProjects.delete(projectId);
      }
    } else {
      this.subscribedProjects.delete(projectId);
    }
  }

  private isCurrentConnectionEpoch(
    connection: HubConnection,
    connectionEpoch: number): boolean {
    return (
      !this.disposed &&
      connection === this.connection &&
      connectionEpoch === this.connectionEpoch
    );
  }

  public async replaceQueueResourceSubscriptions(resources: {
    printerIds: Iterable<string>;
    jobIds: Iterable<string>;
    projectIds: Iterable<string>;
  }): Promise<number> {
    const nextPrinters = new Set(resources.printerIds);
    const nextJobs = new Set(resources.jobIds);
    const nextProjects = new Set(resources.projectIds);
    this.desiredQueuePrinters = nextPrinters;
    this.desiredQueueJobs = nextJobs;
    this.desiredQueueProjects = nextProjects;
    const generation = ++this.queueSubscriptionGeneration;

    await this.enqueueQueueSubscriptionOperation(generation, async () => {
      const operations: Array<() => Promise<void>> = [];
      for (const id of this.subscribedPrinters) {
        if (!nextPrinters.has(id)) {
          operations.push(() => this.applyUnsubscribeFromPrinter(id));
        }
      }
      for (const id of this.subscribedQueueJobs) {
        if (!nextJobs.has(id)) {
          operations.push(() => this.applyUnsubscribeFromQueueJob(id));
        }
      }
      for (const id of this.subscribedProjects) {
        if (!nextProjects.has(id)) {
          operations.push(() => this.applyUnsubscribeFromProject(id));
        }
      }
      for (const id of nextPrinters) {
        if (!this.subscribedPrinters.has(id)) {
          operations.push(() => this.applySubscribeToPrinter(id));
        }
      }
      for (const id of nextJobs) {
        if (!this.subscribedQueueJobs.has(id)) {
          operations.push(() => this.applySubscribeToQueueJob(id));
        }
      }
      for (const id of nextProjects) {
        if (!this.subscribedProjects.has(id)) {
          operations.push(() => this.applySubscribeToProject(id));
        }
      }

      for (const operation of operations) {
        await operation();
        if (this.disposed) {
          this.clearQueueSubscriptionState();
          return;
        }
        if (generation !== this.queueSubscriptionGeneration) {
          return;
        }
      }
    });
    return generation;
  }

  public async releaseQueueResourceSubscriptionsAndDisconnect(): Promise<void> {
    const replacement = this.replaceQueueResourceSubscriptions({
      printerIds: [],
      jobIds: [],
      projectIds: [],
    });
    const releaseGeneration = this.queueSubscriptionGeneration;
    const disconnection = this.disconnect(releaseGeneration);
    await Promise.all([replacement, disconnection]);
  }

  private enqueueQueueSubscriptionOperation(
    generation: number,
    operation: () => Promise<void>
  ): Promise<void> {
    const queued = this.queueSubscriptionTail
      .catch(() => undefined)
      .then(async () => {
        if (this.disposed || generation !== this.queueSubscriptionGeneration) {
          return;
        }
        await operation();
      });
    this.queueSubscriptionTail = queued.catch(() => undefined);
    return queued;
  }

  public onQueueEvent(callback: QueueEventCallback): () => void {
    this.queueEventCallbacks.push(callback);
    return () => {
      const index = this.queueEventCallbacks.indexOf(callback);
      if (index >= 0) this.queueEventCallbacks.splice(index, 1);
    };
  }

  public onQueueResourcesChanged(
    callback: QueueResourcesChangedCallback
  ): () => void {
    this.queueResourcesChangedCallbacks.push(callback);
    return () => {
      const index = this.queueResourcesChangedCallbacks.indexOf(callback);
      if (index >= 0) this.queueResourcesChangedCallbacks.splice(index, 1);
    };
  }

  public getQueueSubscriptionSnapshot(): {
    printerIds: string[];
    jobIds: string[];
    projectIds: string[];
    lastSequence: number;
  } {
    return {
      printerIds: [...this.subscribedPrinters].sort(),
      jobIds: [...this.subscribedQueueJobs].sort(),
      projectIds: [...this.subscribedProjects].sort(),
      lastSequence: this.lastQueueSequence,
    };
  }

  private async handleQueueEvent(event: QueueEventEnvelope): Promise<void> {
    if (event.sequence > this.lastQueueSequence + 1) {
      let cursor = this.lastQueueSequence;
      let hasMore = true;
      while (hasMore && cursor < event.sequence) {
        const feed = await apiClient.getQueueChanges(cursor);
        for (const missed of feed.events) {
          if (missed.sequence > this.lastQueueSequence) {
            this.emitQueueEvent(missed);
            this.lastQueueSequence = missed.sequence;
          }
        }
        if (feed.nextSequence <= cursor) {
          break;
        }
        cursor = feed.nextSequence;
        this.lastQueueSequence = Math.max(this.lastQueueSequence, cursor);
        hasMore = feed.hasMore;
      }
    }

    if (event.sequence > this.lastQueueSequence) {
      this.emitQueueEvent(event);
      this.lastQueueSequence = event.sequence;
    }
  }

  private async drainQueueChanges(): Promise<void> {
    if (this.queueDrain) {
      await this.queueDrain;
      return;
    }

    this.queueDrain = this.drainQueueChangesCore();
    try {
      await this.queueDrain;
    } finally {
      this.queueDrain = null;
    }
  }

  private async drainQueueChangesCore(): Promise<void> {
    let cursor = this.lastQueueSequence;
    let hasMore = true;
    while (hasMore) {
      const feed = await apiClient.getQueueChanges(cursor);
      for (const event of feed.events) {
        if (event.sequence > this.lastQueueSequence) {
          this.emitQueueEvent(event);
          this.lastQueueSequence = event.sequence;
        }
      }

      if (feed.nextSequence <= cursor) {
        break;
      }

      cursor = feed.nextSequence;
      this.lastQueueSequence = Math.max(this.lastQueueSequence, cursor);
      hasMore = feed.hasMore;
    }
  }

  private emitQueueEvent(event: QueueEventEnvelope): void {
    this.queueEventCallbacks.forEach((callback) => {
      try {
        callback(event);
      } catch (error) {
        console.error("Queue event callback error:", error);
      }
    });
  }

  private async restoreResourceSubscriptions(
    connection: HubConnection,
    connectionEpoch: number
  ): Promise<void> {
    const generation = this.queueSubscriptionGeneration;
    await this.enqueueQueueSubscriptionOperation(generation, async () => {
      if (
        !this.isCurrentConnectionEpoch(connection, connectionEpoch) ||
        connection.state !== HubConnectionState.Connected
      ) {
        return;
      }
      const subscriptions = [
        ...Array.from(this.desiredQueuePrinters, (id) => ({
          id,
          method: "SubscribeToPrinterAsync",
          desiredValues: this.desiredQueuePrinters,
          appliedValues: this.subscribedPrinters,
        })),
        ...Array.from(this.desiredQueueJobs, (id) => ({
          id,
          method: "SubscribeToQueueJobAsync",
          desiredValues: this.desiredQueueJobs,
          appliedValues: this.subscribedQueueJobs,
        })),
        ...Array.from(this.desiredQueueProjects, (id) => ({
          id,
          method: "SubscribeToProjectAsync",
          desiredValues: this.desiredQueueProjects,
          appliedValues: this.subscribedProjects,
        })),
      ];
      const outcomes = await Promise.allSettled(
        subscriptions.map(({ id, method }) =>
          connection.invoke(method, id)
        )
      );
      outcomes.forEach((outcome, index) => {
        const subscription = subscriptions[index];
        if (outcome.status === "fulfilled") {
          if (this.isCurrentConnectionEpoch(connection, connectionEpoch)) {
            subscription.appliedValues.add(subscription.id);
          }
        } else if (
          this.isCurrentConnectionEpoch(connection, connectionEpoch) &&
          generation === this.queueSubscriptionGeneration
        ) {
          subscription.desiredValues.delete(subscription.id);
          subscription.appliedValues.delete(subscription.id);
        }
      });
    });
  }

  private async restoreSubscriptionsAndDrain(
    connection: HubConnection,
    connectionEpoch: number
  ): Promise<void> {
    await this.restoreResourceSubscriptions(connection, connectionEpoch);
    if (!this.isCurrentConnectionEpoch(connection, connectionEpoch)) return;
    await this.drainQueueChanges();
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

  public async disconnect(expectedQueueGeneration?: number): Promise<void> {
    if (
      expectedQueueGeneration !== undefined &&
      (expectedQueueGeneration !== this.queueSubscriptionGeneration ||
        this.hasDesiredQueueSubscriptions())
    ) {
      return;
    }

    this.connectionRequested = false;
    this.connectionIntentGeneration++;
    this.reconnectAttempts = 0;
    this.clearManualReconnectTimer();
    this.invalidateConnectionEpoch();
    const connection = this.connection;
    if (connection) {
      await this.stopConnection(connection);
      if (
        expectedQueueGeneration !== undefined &&
        expectedQueueGeneration !== this.queueSubscriptionGeneration &&
        this.hasDesiredQueueSubscriptions()
      ) {
        await this.connect();
      }
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

  onDispatchUploadProgress(callback: DispatchUploadProgressCallback): () => void {
    this.dispatchUploadProgressCallbacks.push(callback);
    return () => {
      const idx = this.dispatchUploadProgressCallbacks.indexOf(callback);
      if (idx > -1) this.dispatchUploadProgressCallbacks.splice(idx, 1);
    };
  }

  onFailureDetected(callback: FailureDetectionCallback): () => void {
    this.failureDetectionCallbacks.push(callback);
    return () => {
      const idx = this.failureDetectionCallbacks.indexOf(callback);
      if (idx > -1) this.failureDetectionCallbacks.splice(idx, 1);
    };
  }

  onAutoDispatchStateChanged(callback: AutoDispatchStatusCallback): () => void {
    this.autoDispatchStatusCallbacks.push(callback);
    return () => {
      const idx = this.autoDispatchStatusCallbacks.indexOf(callback);
      if (idx > -1) this.autoDispatchStatusCallbacks.splice(idx, 1);
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
    this.disposed = true;
    this.connectionRequested = false;
    this.connectionIntentGeneration++;
    this.reconnectAttempts = 0;
    this.clearManualReconnectTimer();
    this.invalidateConnectionEpoch();
    this.queueSubscriptionGeneration += 1;
    if (this.authListener) {
      window.removeEventListener(AUTH_SESSION_ESTABLISHED_EVENT, this.authListener);
      this.authListener = null;
    }
    this.printerStatusCallbacks = [];
    this.jobQueueUpdateCallbacks = [];
    this.connectionStateCallbacks = [];
    this.discoveryProgressCallbacks = [];
    this.discoveryPrinterFoundCallbacks = [];
    this.discoveryCompletedCallbacks = [];
    this.printerImportProgressCallbacks = [];
    this.dispatchUploadProgressCallbacks = [];
    this.failureDetectionCallbacks = [];
    this.autoDispatchStatusCallbacks = [];
    this.queueEventCallbacks = [];
    this.queueResourcesChangedCallbacks = [];
    this.clearQueueSubscriptionState();
    const connection = this.connection;
    if (connection) {
      void this.stopConnection(connection);
      this.connection = null;
    }
  }

  private clearQueueSubscriptionState(): void {
    this.desiredQueuePrinters.clear();
    this.desiredQueueJobs.clear();
    this.desiredQueueProjects.clear();
    this.clearAppliedQueueSubscriptionState();
  }

  private clearAppliedQueueSubscriptionState(): void {
    this.subscribedPrinters.clear();
    this.subscribedQueueJobs.clear();
    this.subscribedProjects.clear();
  }

  private hasDesiredQueueSubscriptions(): boolean {
    return (
      this.desiredQueuePrinters.size > 0 ||
      this.desiredQueueJobs.size > 0 ||
      this.desiredQueueProjects.size > 0
    );
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
