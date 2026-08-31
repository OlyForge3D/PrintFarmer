import { beforeEach, describe, expect, it, vi } from 'vitest';
import { loadWireContractFixture } from '@/test/wireContracts';
import type {
  AlertCreatedEvent,
  AlertStatusChangedEvent,
  MaintenanceCompletedEvent,
} from '@/types/maintenance';

// -----------------------------------------------------------------------------
// Canonical wire-contract corpus (issue #2257): MaintenanceSignalRService's
// "alertcreated", "alertstatuschanged" and "maintenancecompleted" handlers
// (maintenance hub) are driven from the real serialized payloads captured by
// issue #2238 in fixtures/wire-contracts/api/signalr-events, instead of
// hand-written mock objects. The corpus is loaded byte-identical and never
// edited or normalized here — see src/Web/ReactApp/src/test/wireContracts.ts.
//
// Before this issue, none of these three events had any test coverage at all
// (hand-written or otherwise) — this file is purely additive, following the
// harness established for the printers hub in
// src/services/__tests__/printer-signalr.wireContracts.test.ts (PR #2260).
//
// Two real wire-contract gaps surfaced by generating these fixtures from the
// production serialization path (documented here, not silently patched over
// or fixed as part of this corpus/test task):
//  - `AlertCreatedEvent.scheduleId` is declared as a required `string` on the
//    TS interface, but `MaintenanceAlertEngine.BroadcastAlertCreatedAsync`
//    never sends a `scheduleId` key on the wire at all (see
//    alertcreated.populated.json). No current consumer reads `.scheduleId`
//    off this event.
//  - `MaintenanceCompletedEvent` declares an optional `scheduleId`, but the
//    real broadcaster (`MaintenanceResolutionNotifier`) sends this same value
//    under the key `deploymentId`, not `scheduleId`. In this fixture the
//    value is null and therefore omitted from the wire entirely (see
//    maintenancecompleted.populated.json), so the name mismatch does not
//    surface as a missing-field assertion failure here, but it is a real,
//    pre-existing contract drift worth a follow-up finding.
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

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: vi.fn(() => 'http://localhost:5245/hubs/maintenance'),
}));

describe('MaintenanceSignalRService — canonical wire-contract corpus (#2257)', () => {
  beforeEach(async () => {
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
    window.PrintFarmerDebug = undefined;
  });

  it('delivers the corpus populated alertcreated fixture unchanged to onAlertCreated subscribers', async () => {
    const fixture = loadWireContractFixture<AlertCreatedEvent>(
      'api/signalr-events/alertcreated.populated.json'
    );
    const { maintenanceSignalRService } = await import('@/services/maintenance-signalr');
    await maintenanceSignalRService.start();

    const received: AlertCreatedEvent[] = [];
    maintenanceSignalRService.onAlertCreated((event) => received.push(event));

    signalRTestState.connectionHandlers.get('alertcreated')?.(fixture);

    expect(received).toHaveLength(1);
    expect(received[0]).toEqual(fixture);

    await maintenanceSignalRService.stop();
  });

  it('delivers the corpus resolved alertstatuschanged fixture unchanged to onAlertStatusChanged subscribers', async () => {
    const fixture = loadWireContractFixture<AlertStatusChangedEvent>(
      'api/signalr-events/alertstatuschanged.resolved.json'
    );
    const { maintenanceSignalRService } = await import('@/services/maintenance-signalr');
    await maintenanceSignalRService.start();

    const received: AlertStatusChangedEvent[] = [];
    maintenanceSignalRService.onAlertStatusChanged((event) => received.push(event));

    signalRTestState.connectionHandlers.get('alertstatuschanged')?.(fixture);

    expect(received).toHaveLength(1);
    expect(received[0]).toEqual(fixture);
    expect(received[0].status).toBe('Resolved');
    // No acknowledgedAt/dismissedAt keys are sent for a direct resolution —
    // asserting their absence guards against silently filling in fields the
    // real wire payload never included (see wireContracts.ts's "never
    // reshape a fixture" rule).
    expect(received[0]).not.toHaveProperty('acknowledgedAt');
    expect(received[0]).not.toHaveProperty('dismissedAt');

    await maintenanceSignalRService.stop();
  });

  it('delivers the corpus populated maintenancecompleted fixture unchanged to onMaintenanceCompleted subscribers', async () => {
    const fixture = loadWireContractFixture<MaintenanceCompletedEvent>(
      'api/signalr-events/maintenancecompleted.populated.json'
    );
    const { maintenanceSignalRService } = await import('@/services/maintenance-signalr');
    await maintenanceSignalRService.start();

    const received: MaintenanceCompletedEvent[] = [];
    maintenanceSignalRService.onMaintenanceCompleted((event) => received.push(event));

    signalRTestState.connectionHandlers.get('maintenancecompleted')?.(fixture);

    expect(received).toHaveLength(1);
    expect(received[0]).toEqual(fixture);

    await maintenanceSignalRService.stop();
  });
});
