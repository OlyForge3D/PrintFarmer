import { beforeEach, describe, expect, it, vi } from 'vitest';
import { loadWireContractFixture } from '@/test/wireContracts';
import type { PrinterStatusUpdate, DiscoveryProgressDto } from '@/types/api';

// -----------------------------------------------------------------------------
// Canonical wire-contract corpus (issue #2240): PrinterSignalRService's
// "printerupdated" and "discoveryprogress" handlers are driven from the real
// serialized payloads captured by issue #2238 in
// fixtures/wire-contracts/api/printer-status and
// fixtures/wire-contracts/api/signalr-events, instead of hand-written mock
// objects. The corpus is loaded byte-identical and never edited or
// normalized here — see src/Web/ReactApp/src/test/wireContracts.ts.
//
// This harness mirrors the existing mock pattern in
// src/services/__tests__/printer-signalr.test.ts (dispatch the SignalR
// "on(eventName, handler)" registration through a hoisted fake connection).
// -----------------------------------------------------------------------------

const signalRTestState = vi.hoisted(() => {
  const connectionHandlers = new Map<string, (...args: unknown[]) => void>();
  const connection = {
    on: vi.fn((eventName: string, callback: (...args: unknown[]) => void) => {
      connectionHandlers.set(eventName, callback);
    }),
    onclose: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    invoke: vi.fn().mockResolvedValue(undefined),
    state: 'Disconnected',
    connectionId: null,
  };

  const builder = {
    withUrl: vi.fn(),
    withAutomaticReconnect: vi.fn(),
    configureLogging: vi.fn(),
    build: vi.fn(),
  };

  builder.withUrl.mockReturnValue(builder);
  builder.withAutomaticReconnect.mockReturnValue(builder);
  builder.configureLogging.mockReturnValue(builder);
  builder.build.mockReturnValue(connection);

  return {
    connectionHandlers,
    connection,
    builder,
    getSettings: vi.fn(),
    getQueueChanges: vi.fn(),
    getQueueChangeWatermark: vi.fn(),
  };
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: function MockHubConnectionBuilder() {
    return signalRTestState.builder;
  },
  HubConnectionState: {
    Connected: 'Connected',
    Connecting: 'Connecting',
    Reconnecting: 'Reconnecting',
    Disconnecting: 'Disconnecting',
    Disconnected: 'Disconnected',
  },
  LogLevel: {
    Trace: 0,
    Debug: 1,
    Warning: 3,
    Error: 4,
    Critical: 5,
    None: 6,
    Information: 2,
  },
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getSettings: signalRTestState.getSettings,
    getQueueChanges: signalRTestState.getQueueChanges,
    getQueueChangeWatermark: signalRTestState.getQueueChangeWatermark,
  },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: vi.fn(() => 'http://localhost:5245/hubs/printers'),
  getSignalRAccessToken: vi.fn(() => localStorage.getItem('auth-token') || ''),
}));

const flushMicrotasks = async () => {
  for (let index = 0; index < 12; index++) {
    await Promise.resolve();
  }
};

describe('PrinterSignalRService — canonical wire-contract corpus (#2240)', () => {
  beforeEach(() => {
    vi.useRealTimers();
    vi.resetModules();
    vi.clearAllMocks();
    localStorage.clear();
    signalRTestState.connectionHandlers.clear();
    signalRTestState.connection.state = 'Disconnected';
    signalRTestState.connection.start.mockImplementation(async () => {
      signalRTestState.connection.state = 'Connected';
    });
    signalRTestState.connection.stop.mockImplementation(async () => {
      signalRTestState.connection.state = 'Disconnected';
    });
    signalRTestState.connection.invoke.mockResolvedValue(undefined);
    signalRTestState.getSettings.mockResolvedValue({
      logLevel: 'Information',
      consoleLoggingEnabled: false,
    });
    signalRTestState.getQueueChanges.mockResolvedValue({
      afterSequence: 0,
      nextSequence: 0,
      hasMore: false,
      events: [],
    });
    signalRTestState.getQueueChangeWatermark.mockResolvedValue({ latestSequence: 0 });
    window.PrintFarmerDebug = undefined;
  });

  it('delivers the corpus populated printerupdated fixture unchanged to onPrinterStatusUpdate subscribers', async () => {
    const fixture = loadWireContractFixture<PrinterStatusUpdate>(
      'api/printer-status/printerupdated.populated.json'
    );
    const { printerSignalRService } = await import('@/services/printer-signalr');
    await flushMicrotasks();

    const received: PrinterStatusUpdate[] = [];
    printerSignalRService.onPrinterStatusUpdate((status) => received.push(status));

    signalRTestState.connectionHandlers.get('printerupdated')?.(fixture);

    expect(received).toHaveLength(1);
    expect(received[0]).toEqual(fixture);
    expect(printerSignalRService.getLastStatus(fixture.id)).toEqual(fixture);

    printerSignalRService.dispose();
  });

  it('delivers the corpus missing-key printerupdated fixture (offline printer with no telemetry) once the offline-flicker grace period elapses', async () => {
    vi.useFakeTimers();
    try {
      const fixture = loadWireContractFixture<PrinterStatusUpdate>(
        'api/printer-status/printerupdated.missing-key.json'
      );
      const { printerSignalRService } = await import('@/services/printer-signalr');
      await vi.advanceTimersByTimeAsync(0);

      const received: PrinterStatusUpdate[] = [];
      printerSignalRService.onPrinterStatusUpdate((status) => received.push(status));

      signalRTestState.connectionHandlers.get('printerupdated')?.(fixture);

      // isOnline: false with no cached prior status is treated as a fresh
      // online→offline transition, so the service holds it behind the
      // offline-flicker debounce (issue: brief WS hiccups) instead of
      // broadcasting immediately.
      expect(received).toHaveLength(0);

      await vi.advanceTimersByTimeAsync(1_000);

      expect(received).toHaveLength(1);
      // The corpus fixture omits every optional telemetry field (state,
      // progress, temps, etc.) rather than sending them as null/0 — assert
      // they really are absent, not silently defaulted by the handler.
      expect(fixture.state).toBeUndefined();
      expect(received[0]).toEqual(fixture);

      printerSignalRService.dispose();
    } finally {
      vi.useRealTimers();
    }
  });

  it('delivers the corpus populated discoveryprogress fixture unchanged to onDiscoveryProgress subscribers', async () => {
    const fixture = loadWireContractFixture<DiscoveryProgressDto>(
      'api/signalr-events/discoveryprogress.populated.json'
    );
    const { printerSignalRService } = await import('@/services/printer-signalr');
    await flushMicrotasks();

    const received: DiscoveryProgressDto[] = [];
    printerSignalRService.onDiscoveryProgress((progress) => received.push(progress));

    signalRTestState.connectionHandlers.get('discoveryprogress')?.(fixture);

    expect(received).toHaveLength(1);
    expect(received[0]).toEqual(fixture);

    printerSignalRService.dispose();
  });

  it('delivers the corpus missing-message discoveryprogress fixture (no message field) unchanged', async () => {
    const fixture = loadWireContractFixture<DiscoveryProgressDto>(
      'api/signalr-events/discoveryprogress.missing-message.json'
    );
    const { printerSignalRService } = await import('@/services/printer-signalr');
    await flushMicrotasks();

    const received: DiscoveryProgressDto[] = [];
    printerSignalRService.onDiscoveryProgress((progress) => received.push(progress));

    signalRTestState.connectionHandlers.get('discoveryprogress')?.(fixture);

    expect(fixture.message).toBeUndefined();
    expect(received).toHaveLength(1);
    expect(received[0]).toEqual(fixture);

    printerSignalRService.dispose();
  });
});
