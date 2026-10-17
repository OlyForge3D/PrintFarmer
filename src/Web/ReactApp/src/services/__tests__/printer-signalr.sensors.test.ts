import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { PrinterSensor, PrinterStatusUpdate } from '@/types/api';

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

vi.mock('@/services/settingsApi', () => ({
  fetchSettingsValues: signalRTestState.getSettings,
}));

vi.mock('@/services/api/queueRealtimeApi', () => ({
  getQueueChanges: signalRTestState.getQueueChanges,
  getQueueChangeWatermark: signalRTestState.getQueueChangeWatermark,
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

const sensor = (overrides: Partial<PrinterSensor> = {}): PrinterSensor => ({
  id: 'fan part',
  displayName: 'Part fan',
  kind: 'Fan',
  fanRole: 'PartCooling',
  readings: [{ quantity: 'FanSpeed', value: 40, unit: 'Percent' }],
  available: true,
  writable: [{ quantity: 'FanSpeed', min: 0, max: 100, step: 1, unit: 'Percent' }],
  ...overrides,
});

const statusWith = (sensors: PrinterSensor[]) =>
  ({ id: 'p1', isOnline: true, state: 'Printing', sensors }) as unknown as PrinterStatusUpdate;

describe('PrinterSignalRService sensor-only printerupdated events (#3410)', () => {
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

  it('delivers sensor changes through the shared subscription and cache without extra listeners', async () => {
    const { printerSignalRService } = await import('@/services/printer-signalr');
    await flushMicrotasks();

    const received: PrinterStatusUpdate[] = [];
    printerSignalRService.onPrinterStatusUpdate((s) => received.push(s));
    const handler = signalRTestState.connectionHandlers.get('printerupdated');

    handler?.(statusWith([sensor()]));
    handler?.(statusWith([sensor({ readings: [{ quantity: 'FanSpeed', value: 75, unit: 'Percent' }] })]));
    handler?.(statusWith([sensor({ available: false })]));

    expect(received).toHaveLength(3);
    expect(received[1].sensors?.[0].readings[0].value).toBe(75);
    expect(printerSignalRService.getLastStatus('p1')?.sensors?.[0].available).toBe(false);

    const registrations = signalRTestState.connection.on.mock.calls.filter(([name]) => name === 'printerupdated');
    expect(registrations).toHaveLength(1);
    printerSignalRService.dispose();
  });

  it('keeps one printerupdated listener and the cached sensors across a reconnect', async () => {
    const { printerSignalRService } = await import('@/services/printer-signalr');
    await flushMicrotasks();
    signalRTestState.connectionHandlers.get('printerupdated')?.(statusWith([sensor()]));

    const reconnected = signalRTestState.connection.onreconnected.mock.calls[0]?.[0] as (() => void) | undefined;
    reconnected?.();
    await flushMicrotasks();

    const registrations = signalRTestState.connection.on.mock.calls.filter(([name]) => name === 'printerupdated');
    expect(registrations).toHaveLength(1);
    expect(printerSignalRService.getLastStatuses().get('p1')?.sensors).toHaveLength(1);
    printerSignalRService.dispose();
  });
});
