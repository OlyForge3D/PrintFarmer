import { beforeEach, describe, expect, it, vi } from "vitest";
import type { QueueEventEnvelope } from "@/types/api";

const signalr = vi.hoisted(() => {
  const eventHandlers = new Map<string, (payload: unknown) => void>();
  let reconnectHandler: (() => void) | undefined;
  const connection = {
    state: "Disconnected",
    connectionId: "test-connection",
    start: vi.fn(async () => {
      connection.state = "Connected";
    }),
    stop: vi.fn(async () => {
      connection.state = "Disconnected";
    }),
    invoke: vi.fn(async () => undefined),
    on: vi.fn((name: string, handler: (payload: unknown) => void) => {
      eventHandlers.set(name, handler);
    }),
    onclose: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn((handler: () => void) => {
      reconnectHandler = handler;
    }),
  };
  const builder = {
    withUrl: vi.fn(),
    withAutomaticReconnect: vi.fn(),
    configureLogging: vi.fn(),
    build: vi.fn(() => connection),
  };
  builder.withUrl.mockReturnValue(builder);
  builder.withAutomaticReconnect.mockReturnValue(builder);
  builder.configureLogging.mockReturnValue(builder);
  return {
    builder,
    connection,
    eventHandlers,
    getReconnectHandler: () => reconnectHandler,
  };
});

const api = vi.hoisted(() => ({
  getSettings: vi.fn(async () => ({
    logLevel: "Information",
    consoleLoggingEnabled: false,
  })),
  getQueueChanges: vi.fn(),
}));

vi.mock("@microsoft/signalr", () => ({
  HubConnectionState: {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Disconnecting: "Disconnecting",
    Reconnecting: "Reconnecting",
  },
  LogLevel: {
    Trace: 0,
    Debug: 1,
    Information: 2,
    Warning: 3,
    Error: 4,
    Critical: 5,
    None: 6,
  },
  HubConnectionBuilder: class {
    withUrl(url: string, options: unknown) {
      signalr.builder.withUrl(url, options);
      return this;
    }

    withAutomaticReconnect(options: unknown) {
      signalr.builder.withAutomaticReconnect(options);
      return this;
    }

    configureLogging(options: unknown) {
      signalr.builder.configureLogging(options);
      return this;
    }

    build() {
      return signalr.builder.build();
    }
  },
}));

vi.mock("@/services/api", () => ({
  apiClient: api,
}));

vi.mock("@/common/utils/apiUrlHelpers", () => ({
  getHubUrl: vi.fn(() => "/hubs/printers"),
}));

import { PrinterSignalRService } from "@/services/printer-signalr";

const queueEvent = (sequence: number): QueueEventEnvelope => ({
  schemaVersion: "2",
  eventId: `00000000-0000-0000-0000-${sequence.toString().padStart(12, "0")}`,
  sequence,
  eventType: "PrintFarmer.Queue.JobDispatchStarted.v1",
  occurredAtUtc: "2026-07-27T00:00:00Z",
});

describe("PrinterSignalRService queue cursor recovery", () => {
  beforeEach(() => {
    signalr.connection.state = "Disconnected";
    signalr.connection.start.mockClear();
    signalr.connection.stop.mockClear();
    signalr.connection.invoke.mockReset();
    signalr.connection.invoke.mockResolvedValue(undefined);
    signalr.builder.build.mockClear();
    signalr.eventHandlers.clear();
    api.getQueueChanges.mockReset();
    localStorage.clear();
  });

  it("authenticates the browser connection with the current local-storage JWT", async () => {
    localStorage.setItem("auth-token", "jwt-for-websocket");
    const service = new PrinterSignalRService();
    await vi.waitFor(() =>
      expect(signalr.builder.withUrl).toHaveBeenCalled()
    );

    const [, options] = signalr.builder.withUrl.mock.calls.at(-1) as [
      string,
      { accessTokenFactory: () => string },
    ];
    expect(options.accessTokenFactory()).toBe("jwt-for-websocket");
    service.dispose();
  });

  it("drains on initial connect and again from the cursor on reconnect", async () => {
    api.getQueueChanges
      .mockResolvedValueOnce({
        afterSequence: 0,
        nextSequence: 1,
        hasMore: false,
        events: [queueEvent(1)],
      })
      .mockResolvedValueOnce({
        afterSequence: 1,
        nextSequence: 2,
        hasMore: false,
        events: [queueEvent(2)],
      });
    const service = new PrinterSignalRService();
    const received: QueueEventEnvelope[] = [];
    service.onQueueEvent((event) => received.push(event));
    await vi.waitFor(() =>
      expect(signalr.eventHandlers.has("queueevent")).toBe(true)
    );

    await service.connect();

    expect(api.getQueueChanges).toHaveBeenNthCalledWith(1, 0);
    expect(received.map((event) => event.sequence)).toEqual([1]);

    signalr.getReconnectHandler()?.();
    await vi.waitFor(() =>
      expect(received.map((event) => event.sequence)).toEqual([1, 2])
    );
    expect(api.getQueueChanges).toHaveBeenNthCalledWith(2, 1);
    service.dispose();
  });

  it("drains from zero before delivering a first live event with a gap", async () => {
    api.getQueueChanges.mockResolvedValueOnce({
      afterSequence: 0,
      nextSequence: 2,
      hasMore: false,
      events: [queueEvent(1), queueEvent(2)],
    });
    const service = new PrinterSignalRService();
    const received: QueueEventEnvelope[] = [];
    service.onQueueEvent((event) => received.push(event));
    await vi.waitFor(() =>
      expect(signalr.eventHandlers.has("queueevent")).toBe(true)
    );

    signalr.eventHandlers.get("queueevent")?.(queueEvent(3));

    await vi.waitFor(() =>
      expect(received.map((event) => event.sequence)).toEqual([1, 2, 3])
    );
    expect(api.getQueueChanges).toHaveBeenCalledWith(0);
    service.dispose();
  });

  it("restores authorized printer, job, and project scopes after reconnect", async () => {
    api.getQueueChanges.mockResolvedValue({
      afterSequence: 0,
      nextSequence: 0,
      hasMore: false,
      events: [],
    });
    const service = new PrinterSignalRService();
    await vi.waitFor(() =>
      expect(signalr.eventHandlers.has("queueevent")).toBe(true)
    );
    await service.connect();
    await service.replaceQueueResourceSubscriptions({
      printerIds: ["printer-1"],
      jobIds: ["job-1"],
      projectIds: ["project-1"],
    });
    signalr.connection.invoke.mockClear();

    signalr.getReconnectHandler()?.();

    await vi.waitFor(() => {
      expect(signalr.connection.invoke).toHaveBeenCalledWith(
        "SubscribeToPrinterAsync",
        "printer-1"
      );
      expect(signalr.connection.invoke).toHaveBeenCalledWith(
        "SubscribeToQueueJobAsync",
        "job-1"
      );
      expect(signalr.connection.invoke).toHaveBeenCalledWith(
        "SubscribeToProjectAsync",
        "project-1"
      );
    });
    service.dispose();
  });

  it("prunes a revoked scope and still drains the REST cursor on reconnect", async () => {
    api.getQueueChanges
      .mockResolvedValueOnce({
        afterSequence: 0,
        nextSequence: 1,
        hasMore: false,
        events: [queueEvent(1)],
      })
      .mockResolvedValueOnce({
        afterSequence: 1,
        nextSequence: 2,
        hasMore: false,
        events: [queueEvent(2)],
      });
    const service = new PrinterSignalRService();
    const received: QueueEventEnvelope[] = [];
    service.onQueueEvent((event) => received.push(event));
    await vi.waitFor(() =>
      expect(signalr.eventHandlers.has("queueevent")).toBe(true)
    );
    await service.connect();
    await service.subscribeToQueueJob("job-revoked");
    signalr.connection.invoke.mockImplementation(async (
      method: string
    ): Promise<undefined> => {
      if (method === "SubscribeToQueueJobAsync") {
        throw new Error("resource_forbidden");
      }
      return undefined;
    });

    signalr.getReconnectHandler()?.();

    await vi.waitFor(() =>
      expect(received.map((event) => event.sequence)).toEqual([1, 2])
    );
    expect(api.getQueueChanges).toHaveBeenNthCalledWith(2, 1);
    service.dispose();
  });
});
