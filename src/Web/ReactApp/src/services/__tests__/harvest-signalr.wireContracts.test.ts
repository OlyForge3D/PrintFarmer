import { beforeEach, describe, expect, it, vi } from 'vitest';
import { loadWireContractFixture } from '@/test/wireContracts';
import type { HarvestFileProgress, HarvestFileUpdatedEvent } from '@/services/harvest-signalr';

// -----------------------------------------------------------------------------
// Canonical wire-contract corpus (issue #2257): SignalRService's
// "harvestfileprogress" and "harvestfileupdated" handlers (harvest hub) are
// driven from the real serialized payloads captured by issue #2238 in
// fixtures/wire-contracts/api/signalr-events, instead of hand-written mock
// objects. The corpus is loaded byte-identical and never edited or
// normalized here — see src/Web/ReactApp/src/test/wireContracts.ts.
//
// Before this issue, neither event had any test coverage at all (hand-written
// or otherwise) — this file is purely additive, following the harness
// established for the printers hub in
// src/services/__tests__/printer-signalr.wireContracts.test.ts (PR #2260).
// -----------------------------------------------------------------------------

// src/test/setup.ts globally stubs this module for every test file (to avoid
// real connection attempts elsewhere in the suite). Undo that here so this
// file exercises the real SignalRService class against our own mocked
// @microsoft/signalr connection below — otherwise every subscription method
// resolves to the global stub instead of the real implementation.
vi.unmock('@/services/harvest-signalr');

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
  },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: vi.fn(() => 'http://localhost:5245/hubs/harvest'),
  getSignalRAccessToken: vi.fn(() => localStorage.getItem('auth-token') || ''),
}));

const flushMicrotasks = async () => {
  for (let index = 0; index < 12; index++) {
    await Promise.resolve();
  }
};

describe('SignalRService (harvest hub) — canonical wire-contract corpus (#2257)', () => {
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
    window.PrintFarmerDebug = undefined;
  });

  it('delivers the corpus populated harvestfileprogress fixture unchanged to onHarvestFileProgress subscribers', async () => {
    const fixture = loadWireContractFixture<HarvestFileProgress>(
      'api/signalr-events/harvestfileprogress.populated.json'
    );
    const { signalRService } = await import('@/services/harvest-signalr');
    await flushMicrotasks();

    const received: HarvestFileProgress[] = [];
    signalRService.onHarvestFileProgress((progress) => received.push(progress));

    signalRTestState.connectionHandlers.get('harvestfileprogress')?.(fixture);

    expect(received).toHaveLength(1);
    expect(received[0]).toEqual(fixture);
  });

  it('delivers the corpus skipped harvestfileupdated fixture unchanged to onHarvestFileUpdated subscribers', async () => {
    // NOTE: the real wire payload includes an `isSelected` key that does not
    // appear on the `HarvestFileUpdatedEvent` TS interface at all (it is a
    // real, currently-untyped field on the wire — see
    // fixtures/wire-contracts/api/signalr-events/harvestfileupdated.skipped.json).
    // This corpus consumes the fixture byte-identical rather than papering
    // over that gap, per the "never reshape a fixture" rule in
    // src/Web/ReactApp/src/test/wireContracts.ts.
    const fixture = loadWireContractFixture<HarvestFileUpdatedEvent>(
      'api/signalr-events/harvestfileupdated.skipped.json'
    );
    const { signalRService } = await import('@/services/harvest-signalr');
    await flushMicrotasks();

    const received: HarvestFileUpdatedEvent[] = [];
    signalRService.onHarvestFileUpdated((evt) => received.push(evt));

    signalRTestState.connectionHandlers.get('harvestfileupdated')?.(fixture);

    expect(received).toHaveLength(1);
    expect(received[0]).toEqual(fixture);
    expect(received[0].status).toBe('Skipped');
    expect(received[0].errorMessage).toBe('Skipped by user');
  });
});
