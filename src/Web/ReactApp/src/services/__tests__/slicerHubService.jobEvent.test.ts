import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { SliceJobEvent } from '@/services/slicerHubService';

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
    handlers.get(methodName)?.forEach(handler => handler(...args));
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

function makeEvent(overrides: Partial<SliceJobEvent> = {}): SliceJobEvent {
  return {
    eventType: 'JobProgress',
    jobId: 'job-1',
    userId: 'user-1',
    status: 'Processing',
    timestamp: new Date().toISOString(),
    ...overrides,
  };
}

describe('slicerHubService.onJobEvent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    hubTestState.handlers.clear();
  });

  it('subscribes to the "slicejobevent" method name, not "SliceJob_{jobId}"', async () => {
    // Regression: the server broadcasts every slice job event under the single method
    // name 'slicejobevent' (SliceJobEventService.BroadcastEventAsync). Registering a
    // handler under 'SliceJob_{jobId}' is never invoked by the server, so progress
    // events were silently dropped.
    await slicerHubService.start();

    slicerHubService.onJobEvent('job-1', vi.fn());

    expect(hubTestState.connection.on).toHaveBeenCalledWith('slicejobevent', expect.any(Function));
    expect(hubTestState.connection.on).not.toHaveBeenCalledWith('SliceJob_job-1', expect.any(Function));

    await slicerHubService.stop();
  });

  it('filters events by jobId and ignores events for other jobs', async () => {
    await slicerHubService.start();

    const callback = vi.fn();
    slicerHubService.onJobEvent('job-1', callback);

    hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-2', progressPercent: 10 }));
    expect(callback).not.toHaveBeenCalled();

    hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-1', progressPercent: 42 }));
    expect(callback).toHaveBeenCalledTimes(1);
    expect(callback).toHaveBeenCalledWith(expect.objectContaining({ jobId: 'job-1', progressPercent: 42 }));

    await slicerHubService.stop();
  });

  it('unsubscribes cleanly without affecting other listeners on the same method', async () => {
    await slicerHubService.start();

    const callbackA = vi.fn();
    const callbackB = vi.fn();
    const unsubscribeA = slicerHubService.onJobEvent('job-1', callbackA);
    slicerHubService.onJobEvent('job-2', callbackB);

    unsubscribeA();

    hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-1' }));
    hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-2' }));

    expect(callbackA).not.toHaveBeenCalled();
    expect(callbackB).toHaveBeenCalledTimes(1);

    await slicerHubService.stop();
  });
});
