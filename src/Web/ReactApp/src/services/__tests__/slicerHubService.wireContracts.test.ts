import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { loadWireContractFixture } from '@/test/wireContracts';
import type { SliceJobEvent } from '@/services/slicerHubService';

// -----------------------------------------------------------------------------
// Canonical wire-contract corpus (issue #2240): slicerHubService's
// "SlicerRegistered" and "slicejobevent" handlers are driven from the real
// serialized payloads captured by issue #2238 in
// fixtures/wire-contracts/api/signalr-events, instead of hand-written mock
// objects. The corpus is loaded byte-identical and never edited or
// normalized here — see src/Web/ReactApp/src/test/wireContracts.ts.
//
// This harness mirrors the existing pattern in
// src/services/__tests__/slicerHubService.jobEvent.test.ts (a hoisted fake
// SignalR connection exposing an `emit(methodName, ...)` helper that invokes
// whatever handlers slicerHubService registered via connection.on(...)).
// -----------------------------------------------------------------------------

const hubTestState = vi.hoisted(() => {
  const handlers = new Map<string, Set<(...args: unknown[]) => void>>();
  const connection = {
    on: vi.fn((methodName: string, callback: (...args: unknown[]) => void) => {
      if (!handlers.has(methodName)) handlers.set(methodName, new Set());
      handlers.get(methodName)!.add(callback);
    }),
    off: vi.fn((methodName: string, callback?: (...args: unknown[]) => void) => {
      if (!callback) {
        handlers.delete(methodName);
        return;
      }
      handlers.get(methodName)?.delete(callback);
    }),
    onclose: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    state: 'Disconnected',
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

  const emit = (methodName: string, ...args: unknown[]) => {
    handlers.get(methodName)?.forEach((handler) => handler(...args));
  };

  return { builder, connection, handlers, emit };
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: function MockHubConnectionBuilder() {
    return hubTestState.builder;
  },
  HttpTransportType: { WebSockets: 1, ServerSentEvents: 2, LongPolling: 4 },
  LogLevel: { Information: 2 },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: vi.fn(() => 'http://localhost:5245/hubs/slicers'),
}));

let slicerHubService: typeof import('@/services/slicerHubService')['slicerHubService'];

beforeAll(async () => {
  ({ slicerHubService } = await import('@/services/slicerHubService'));
}, 60_000);

describe('slicerHubService — canonical wire-contract corpus (#2240)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    hubTestState.handlers.clear();
  });

  it('delivers the corpus SlicerRegistered fixture unchanged to onSlicerRegistered subscribers', async () => {
    // NOTE (finding, to be filed separately): the wire corpus for
    // "SlicerRegistered" carries {id, name, slicerType, version,
    // maxConcurrentJobs, status, lastSeen} — it has no `capabilities` field.
    // The TS `SlicerRegisteredEvent` interface instead declares a required
    // `capabilities: string[]` and omits maxConcurrentJobs/status/lastSeen
    // entirely. That is a type/wire drift (same class of bug as #2232), so
    // this test intentionally does NOT type the fixture as
    // `SlicerRegisteredEvent` — asserting against that interface would force
    // us to either fabricate a `capabilities` field the server never sends,
    // or silently paper over the mismatch. We load it as `unknown` and
    // assert pass-through instead.
    const fixture = loadWireContractFixture<Record<string, unknown>>(
      'api/signalr-events/SlicerRegistered.populated.json'
    );
    expect(fixture).not.toHaveProperty('capabilities');

    await slicerHubService.start();

    const received: unknown[] = [];
    slicerHubService.onSlicerRegistered((event) => received.push(event));

    hubTestState.emit('SlicerRegistered', fixture);

    expect(received).toHaveLength(1);
    expect(received[0]).toEqual(fixture);

    await slicerHubService.stop();
  });

  it('delivers the corpus queued slicejobevent fixture unchanged to onJobEvent subscribers for the matching jobId', async () => {
    const fixture = loadWireContractFixture<SliceJobEvent>(
      'api/signalr-events/slicejobevent.queued.json'
    );

    await slicerHubService.start();

    const received: SliceJobEvent[] = [];
    slicerHubService.onJobEvent(fixture.jobId, (event) => received.push(event));

    hubTestState.emit('slicejobevent', fixture);

    expect(received).toHaveLength(1);
    // The corpus fixture also carries a server-side `priority` field that has
    // no counterpart in the `SliceJobEvent` TS interface — an additive field
    // the client is expected to ignore, so pass-through equality (not a
    // subset match) proves the handler neither strips nor mutates it.
    expect(received[0]).toEqual(fixture);

    await slicerHubService.stop();
  });

  it('ignores the corpus slicejobevent fixture when subscribed to a different jobId', async () => {
    const fixture = loadWireContractFixture<SliceJobEvent>(
      'api/signalr-events/slicejobevent.queued.json'
    );

    await slicerHubService.start();

    const callback = vi.fn();
    slicerHubService.onJobEvent('some-other-job-id', callback);

    hubTestState.emit('slicejobevent', fixture);

    expect(callback).not.toHaveBeenCalled();

    await slicerHubService.stop();
  });
});
