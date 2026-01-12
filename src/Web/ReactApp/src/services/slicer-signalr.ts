import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import { apiClient } from "@/services/api";
import { getHubUrl } from "@/common/utils/apiUrlHelpers";

// Slicer progress update event
export interface SlicingProgressUpdate {
  jobId: string;
  progress: number;
  message?: string;
  currentLayer?: number;
  totalLayers?: number;
  estimatedTimeRemainingSeconds?: number;
}

// Slicer completion notification
export interface SlicingCompletionNotification {
  jobId: string;
  userId: string;
  status: string;
  success: boolean;
  resultFileUrl?: string;
  processingTimeSeconds: number;
  estimatedPrintTimeSeconds?: number;
  estimatedFilamentUsageGrams?: number;
  layerCount?: number;
  errorMessage?: string;
  completedAt: string;
  metadata?: Record<string, unknown>;
}

// Slicer failure notification
export interface SlicingFailureNotification {
  jobId: string;
  userId: string;
  errorMessage: string;
  failedAt: string;
  metadata?: Record<string, unknown>;
}

// Slice job event (from SliceJobEventService)
export interface SliceJobEvent {
  eventType: string;
  jobId: string;
  userId: string;
  printerId?: string;
  status: string;
  progressPercent: number;
  progressMessage?: string;
  queuedAt: string;
  startedAt?: string;
  completedAt?: string;
  resultFileUrl?: string;
  errorMessage?: string;
  estimatedPrintTimeSeconds?: number;
  filamentUsedGrams?: number;
  workerId?: string;
  priority: number;
  timestamp: string;
}

type ProgressCallback = (update: SlicingProgressUpdate) => void;
type CompletionCallback = (notification: SlicingCompletionNotification) => void;
type FailureCallback = (notification: SlicingFailureNotification) => void;
type JobEventCallback = (event: SliceJobEvent) => void;
type ConnectionStateCallback = (connected: boolean) => void;

export class SlicerSignalRService {
  private connection: HubConnection | null = null;
  private reconnectAttempts = 0;
  private maxReconnectAttempts = 5;
  private reconnectDelay = 1000;
  private maxReconnectDelay = 30000;
  private signalrSettings: {
    logLevel: string;
    consoleLoggingEnabled: boolean;
  } | null = null;

  private progressCallbacks: ProgressCallback[] = [];
  private completionCallbacks: CompletionCallback[] = [];
  private failureCallbacks: FailureCallback[] = [];
  private jobEventCallbacks: JobEventCallback[] = [];
  private connectionStateCallbacks: ConnectionStateCallback[] = [];

  constructor() {
    this.loadSettings().then(() => {
      this.buildConnection();
    });
  }

  private async loadSettings(): Promise<void> {
    try {
      this.signalrSettings = await apiClient.getSettings<{
        logLevel: string;
        consoleLoggingEnabled: boolean;
      }>("SignalR");
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

  private buildConnection(): void {
    const slicerSignalrUrl = getHubUrl("/hubs/slicer");

    this.connection = new HubConnectionBuilder()
      .withUrl(slicerSignalrUrl)
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
      .configureLogging(this.getLogLevel())
      .build();

    this.setupEventHandlers();
  }

  private setupEventHandlers(): void {
    if (!this.connection) return;

    // Slicer progress events
    this.connection.on("slicingprogress", (update: SlicingProgressUpdate) => {
      this.progressCallbacks.forEach((cb) => {
        try {
          cb(update);
        } catch (e) {
          console.error("Progress cb error:", e);
        }
      });
    });

    // Slicer completion events
    this.connection.on(
      "slicingcompleted",
      (notification: SlicingCompletionNotification) => {
        this.completionCallbacks.forEach((cb) => {
          try {
            cb(notification);
          } catch (e) {
            console.error("Completion cb error:", e);
          }
        });
      }
    );

    // Slicer failure events
    this.connection.on(
      "slicingfailed",
      (notification: SlicingFailureNotification) => {
        this.failureCallbacks.forEach((cb) => {
          try {
            cb(notification);
          } catch (e) {
            console.error("Failure cb error:", e);
          }
        });
      }
    );

    // Slice job lifecycle events (from SliceJobEventService)
    this.connection.on("slicejobevent", (event: SliceJobEvent) => {
      this.jobEventCallbacks.forEach((cb) => {
        try {
          cb({ ...event, eventType: "JobQueued" });
        } catch (e) {
          console.error("Job event cb error:", e);
        }
      });
    });

    this.connection.on("slicejobevent", (event: SliceJobEvent) => {
      this.jobEventCallbacks.forEach((cb) => {
        try {
          cb({ ...event, eventType: "JobStarted" });
        } catch (e) {
          console.error("Job event cb error:", e);
        }
      });
    });

    this.connection.on("slicejobevent", (event: SliceJobEvent) => {
      this.jobEventCallbacks.forEach((cb) => {
        try {
          cb({ ...event, eventType: "JobProgress" });
        } catch (e) {
          console.error("Job event cb error:", e);
        }
      });
    });

    this.connection.on("slicejobevent", (event: SliceJobEvent) => {
      this.jobEventCallbacks.forEach((cb) => {
        try {
          cb({ ...event, eventType: "JobCompleted" });
        } catch (e) {
          console.error("Job event cb error:", e);
        }
      });
    });

    this.connection.on("slicejobevent", (event: SliceJobEvent) => {
      this.jobEventCallbacks.forEach((cb) => {
        try {
          cb({ ...event, eventType: "JobFailed" });
        } catch (e) {
          console.error("Job event cb error:", e);
        }
      });
    });

    this.connection.on("slicejobevent", (event: SliceJobEvent) => {
      this.jobEventCallbacks.forEach((cb) => {
        try {
          cb({ ...event, eventType: "JobCancelled" });
        } catch (e) {
          console.error("Job event cb error:", e);
        }
      });
    });

    // Connection lifecycle
    this.connection.onclose(() => this.notifyConnectionState(false));
    this.connection.onreconnecting(() => this.notifyConnectionState(false));
    this.connection.onreconnected(() => {
      this.reconnectAttempts = 0;
      this.notifyConnectionState(true);
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
    if (!this.connection) this.buildConnection();
    if (this.connection!.state === HubConnectionState.Connected) return;
    if (this.connection!.state === HubConnectionState.Connecting) return;
    if (this.connection!.state !== HubConnectionState.Disconnected) return;

    try {
      await this.connection!.start();
      this.reconnectAttempts = 0;
      this.notifyConnectionState(true);
    } catch (error) {
      console.error("[slicerSignalR] connect failed", error);
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

  public async disconnect(): Promise<void> {
    if (
      this.connection &&
      this.connection.state === HubConnectionState.Connected
    ) {
      await this.connection.stop();
    }
  }

  /**
   * Subscribe to a specific job's events
   */
  public async subscribeToJob(jobId: string): Promise<void> {
    if (
      this.connection &&
      this.connection.state === HubConnectionState.Connected
    ) {
      await this.connection.invoke("SubscribeToJob", jobId);
    }
  }

  /**
   * Unsubscribe from a specific job's events
   */
  public async unsubscribeFromJob(jobId: string): Promise<void> {
    if (
      this.connection &&
      this.connection.state === HubConnectionState.Connected
    ) {
      await this.connection.invoke("UnsubscribeFromJob", jobId);
    }
  }

  /**
   * Join the monitoring group to receive all job events
   */
  public async joinMonitoringGroup(): Promise<void> {
    if (
      this.connection &&
      this.connection.state === HubConnectionState.Connected
    ) {
      await this.connection.invoke("JoinMonitoringGroup");
    }
  }

  /**
   * Leave the monitoring group
   */
  public async leaveMonitoringGroup(): Promise<void> {
    if (
      this.connection &&
      this.connection.state === HubConnectionState.Connected
    ) {
      await this.connection.invoke("LeaveMonitoringGroup");
    }
  }

  // Event subscription methods
  public onProgress(callback: ProgressCallback): () => void {
    this.progressCallbacks.push(callback);
    return () => {
      const idx = this.progressCallbacks.indexOf(callback);
      if (idx > -1) this.progressCallbacks.splice(idx, 1);
    };
  }

  public onCompletion(callback: CompletionCallback): () => void {
    this.completionCallbacks.push(callback);
    return () => {
      const idx = this.completionCallbacks.indexOf(callback);
      if (idx > -1) this.completionCallbacks.splice(idx, 1);
    };
  }

  public onFailure(callback: FailureCallback): () => void {
    this.failureCallbacks.push(callback);
    return () => {
      const idx = this.failureCallbacks.indexOf(callback);
      if (idx > -1) this.failureCallbacks.splice(idx, 1);
    };
  }

  public onJobEvent(callback: JobEventCallback): () => void {
    this.jobEventCallbacks.push(callback);
    return () => {
      const idx = this.jobEventCallbacks.indexOf(callback);
      if (idx > -1) this.jobEventCallbacks.splice(idx, 1);
    };
  }

  public onConnectionStateChange(
    callback: ConnectionStateCallback
  ): () => void {
    this.connectionStateCallbacks.push(callback);
    return () => {
      const idx = this.connectionStateCallbacks.indexOf(callback);
      if (idx > -1) this.connectionStateCallbacks.splice(idx, 1);
    };
  }

  // Connection state properties
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
    this.progressCallbacks = [];
    this.completionCallbacks = [];
    this.failureCallbacks = [];
    this.jobEventCallbacks = [];
    this.connectionStateCallbacks = [];

    if (this.connection) {
      this.connection.stop();
      this.connection = null;
    }
  }
}

export const slicerSignalRService = new SlicerSignalRService();
