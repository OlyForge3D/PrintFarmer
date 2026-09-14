import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import { PrinterControlTracker, isControlOperationResolved } from '@/services/printer-control-operations';
import type { PrinterControlCurrent, PrinterControlIntent, PrinterControlOperation } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    createPrinterControlOperation: vi.fn(),
    getCurrentPrinterControlOperation: vi.fn(),
    getPrinterControlOperation: vi.fn(),
  },
}));

const printerId = '11111111-1111-4111-8111-111111111111';
const operationId = '22222222-2222-4222-8222-222222222222';
const successorId = '33333333-3333-4333-8333-333333333333';
const storageKey = 'printfarmer:control-operation:old-server-subject-printer';
const intent = { kind: 'HomeAll' as const };
const operation = (overrides: Partial<PrinterControlOperation> = {}): PrinterControlOperation => ({
  printerId, operationId, kind: 'HomeAll', x: null, y: null, z: null, f: null,
  state: 'Running', rowVersion: 'opaque-v1', createdAtUtc: '2026-09-12T18:00:00Z', updatedAtUtc: '2026-09-12T18:00:00Z',
  startedAtUtc: '2026-09-12T18:00:00Z', completedAtUtc: null,
  barrierHeld: true, requiresRecovery: false, completionEvidence: 'None', failure: null,
  senderIsolation: 'NotRequested', ...overrides,
});
function current(op: PrinterControlOperation | null = null): PrinterControlCurrent {
  if (!op?.barrierHeld) op = null;
  return {
    physicalControl: {
      supportedOperations: ['HomeAll', 'HomeXY', 'HomeZ', 'Jog', 'MoveTo'],
      barrierHeld: op?.barrierHeld ?? false, operationId: op?.operationId ?? null,
      state: op?.state ?? null, requiresRecovery: op?.requiresRecovery ?? false,
    }, operation: op,
  };
}
const completed = () => operation({ state: 'Succeeded', barrierHeld: false, completionEvidence: 'MotionQueueDrained' });
let active: PrinterControlOperation | null;
let sessionCurrent: boolean;
let tracker: PrinterControlTracker;

beforeEach(() => {
  vi.useFakeTimers();
  vi.clearAllMocks();
  localStorage.clear();
  active = null;
  sessionCurrent = true;
  vi.spyOn(crypto, 'randomUUID').mockReturnValue(operationId);
  vi.mocked(apiClient.getCurrentPrinterControlOperation).mockImplementation(async () => current(active));
  vi.mocked(apiClient.getPrinterControlOperation).mockImplementation(async () => ({ operation: active!, etag: '"v1"' }));
  vi.mocked(apiClient.createPrinterControlOperation).mockImplementation(async () => {
    active = operation();
    return { operation: active, etag: '"v1"' };
  });
  tracker = new PrinterControlTracker(printerId, () => sessionCurrent);
});
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); vi.useRealTimers(); });

describe('session motion coordination without persistent recovery gates', () => {
  it.each(['{"operationId":"legacy","admissionConfirmed":true}', 'malformed receipt'])(
    'ignores obsolete persisted receipts (%s) instead of restoring a lockout', async receipt => {
      localStorage.setItem(storageKey, receipt);
      tracker = new PrinterControlTracker(printerId, () => true);
      await tracker.refresh();
      expect(tracker.isBlocked()).toBe(false);
      expect(tracker.getSnapshot().saved).toBeNull();
      expect(apiClient.getPrinterControlOperation).not.toHaveBeenCalled();
      expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
    });

  it('works without readable or writable browser storage', async () => {
    const read = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('denied'); });
    const write = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('quota'); });
    tracker = new PrinterControlTracker(printerId, () => true);
    await tracker.refresh();
    const task = tracker.execute(intent, new AbortController().signal);
    await vi.advanceTimersByTimeAsync(0);
    active = completed();
    await vi.advanceTimersByTimeAsync(1_000);
    expect(await task).toEqual({ success: true });
    expect(read).not.toHaveBeenCalled();
    expect(write).not.toHaveBeenCalled();
  });

  it.each(['Queued', 'Running'] as const)('blocks a genuinely active %s command after reopening', async state => {
    active = operation({ state });
    await tracker.refresh();
    expect(tracker.isBlocked()).toBe(true);
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow();
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
    active = operation({ state: 'Unknown', barrierHeld: false });
    await tracker.refresh();
    expect(tracker.isBlocked()).toBe(false);
    expect(tracker.getSnapshot().operation?.state).toBe('Unknown');
  });

  it('blocks a non-manual active owner even when there is no manual receipt', async () => {
    const busy = current();
    busy.physicalControl.barrierHeld = true;
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockResolvedValue(busy);
    await tracker.refresh();
    expect(tracker.isBlocked()).toBe(true);
    expect(apiClient.getPrinterControlOperation).not.toHaveBeenCalled();
  });

  it('keeps a newly admitted active receipt blocking when the preceding current read was idle', async () => {
    await tracker.refresh();
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockResolvedValueOnce(current());
    const controller = new AbortController();
    const task = tracker.execute(intent, controller.signal).catch(error => error);
    await vi.advanceTimersByTimeAsync(0);
    expect(tracker.getSnapshot().current?.physicalControl.barrierHeld).toBe(false);
    expect(tracker.getSnapshot().operation?.state).toBe('Running');
    expect(tracker.isBlocked()).toBe(true);
    controller.abort();
    await task;
  });

  it.each(['Unknown', 'Recovering', 'Recovered', 'Failed', 'Succeeded'] as const)(
    'settles %s without isolation or recovery prerequisites and without fabricating success', async state => {
      await tracker.refresh();
      const task = tracker.execute(intent, new AbortController().signal);
      await vi.advanceTimersByTimeAsync(0);
      active = operation({ state, barrierHeld: false, requiresRecovery: true, senderIsolation: 'Pending' });
      await vi.advanceTimersByTimeAsync(1_000);
      expect(await task).toMatchObject({ success: false });
      expect(tracker.getSnapshot().operation?.state).toBe(state);
      expect(tracker.getSnapshot().saved).toBeNull();
      expect(tracker.isBlocked()).toBe(false);
      expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
      expect(isControlOperationResolved(active)).toBe(true);
    });

  it('does not use legacy requiresRecovery on current status as a gate', async () => {
    const idle = current();
    idle.physicalControl.requiresRecovery = true;
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockResolvedValue(idle);
    await tracker.refresh();
    expect(tracker.isBlocked()).toBe(false);
  });

  it('preserves a successor barrier after the submitted command settles Unknown', async () => {
    await tracker.refresh();
    const task = tracker.execute(intent, new AbortController().signal);
    await vi.advanceTimersByTimeAsync(0);
    const unknown = operation({ state: 'Unknown', barrierHeld: false });
    active = operation({ operationId: successorId });
    vi.mocked(apiClient.getPrinterControlOperation).mockResolvedValue({ operation: unknown, etag: '"v2"' });
    await vi.advanceTimersByTimeAsync(1_000);
    expect(await task).toMatchObject({ success: false });
    expect(tracker.getSnapshot().operation?.operationId).toBe(successorId);
    expect(tracker.isBlocked()).toBe(true);
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
  });

  it.each([404, 410])('a missing receipt (%s) never creates a permanent gate or a retry', async statusCode => {
    await tracker.refresh();
    vi.mocked(apiClient.createPrinterControlOperation).mockRejectedValueOnce(new Error('lost response'));
    vi.mocked(apiClient.getPrinterControlOperation).mockRejectedValue({ statusCode });
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow('no command was retried');
    expect(tracker.getSnapshot().error).toContain('outcome is unknown');
    expect(tracker.getSnapshot().saved).toBeNull();
    expect(tracker.isBlocked()).toBe(false);
    await tracker.refresh();
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
    expect(tracker).not.toHaveProperty('retryAdmission');
    expect(tracker).not.toHaveProperty('recover');
  });

  it('lost admission still tracks a command actually running on the server, without retry', async () => {
    await tracker.refresh();
    vi.mocked(apiClient.createPrinterControlOperation).mockImplementationOnce(async () => {
      active = operation();
      throw new Error('lost response');
    });
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow('no command was retried');
    expect(tracker.isBlocked()).toBe(true);
    active = operation({ state: 'Unknown', barrierHeld: false });
    await tracker.refresh();
    expect(tracker.isBlocked()).toBe(false);
    expect(tracker.getSnapshot().operation?.state).toBe('Unknown');
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
  });

  it('serializes simultaneous submissions from shared views in memory', async () => {
    await tracker.refresh();
    const controller = new AbortController();
    const first = tracker.execute(intent, controller.signal).catch(error => error);
    await expect(tracker.execute(intent, controller.signal)).rejects.toThrow();
    await vi.advanceTimersByTimeAsync(0);
    expect(tracker.isBlocked()).toBe(true);
    controller.abort();
    await first;
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
  });

  it('discards a pre-reservation background read instead of replacing the new operation', async () => {
    await tracker.refresh();
    let read!: (value: PrinterControlCurrent) => void;
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockReturnValueOnce(new Promise(resolve => { read = resolve; }));
    const background = tracker.refresh();
    const controller = new AbortController();
    const task = tracker.execute(intent, controller.signal).catch(error => error);
    read(current());
    await background;
    await vi.advanceTimersByTimeAsync(0);
    expect(tracker.getSnapshot().saved?.operationId).toBe(operationId);
    expect(tracker.getSnapshot().operation?.state).toBe('Running');
    expect(tracker.isBlocked()).toBe(true);
    controller.abort();
    await task;
  });

  it.each(['x', 'y', 'z', 'f'] as const)('rejects mismatched %s completion without advancing the caller', async axis => {
    const move: PrinterControlIntent = { kind: 'MoveTo', x: 10, y: 20, z: 30, f: 100 };
    await tracker.refresh();
    vi.mocked(apiClient.createPrinterControlOperation).mockImplementationOnce(async () => {
      active = operation({ ...move });
      return { operation: active, etag: '"v1"' };
    });
    const task = tracker.execute(move, new AbortController().signal).catch(error => error);
    await vi.advanceTimersByTimeAsync(0);
    active = operation({ ...completed(), ...move, [axis]: 99 });
    await vi.advanceTimersByTimeAsync(1_000);
    expect(await task).toBeInstanceOf(Error);
    expect(tracker.getCompletedOperation()).toBeNull();
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
  });

  it('does not treat a POST success as canonical completion', async () => {
    await tracker.refresh();
    vi.mocked(apiClient.createPrinterControlOperation).mockResolvedValue({ operation: completed(), etag: null });
    active = operation();
    const controller = new AbortController();
    const task = tracker.execute(intent, controller.signal).catch(error => error);
    await vi.advanceTimersByTimeAsync(0);
    expect(tracker.getCompletedOperation()).toBeNull();
    expect(tracker.isBlocked()).toBe(true);
    controller.abort();
    await task;
  });

  it('generates one secure id without randomUUID and never persists it', async () => {
    const getRandomValues = vi.fn(crypto.getRandomValues.bind(crypto));
    vi.stubGlobal('crypto', { getRandomValues });
    await tracker.refresh();
    vi.mocked(apiClient.createPrinterControlOperation).mockImplementationOnce(async (_id, generatedId) => {
      active = operation({ ...completed(), operationId: generatedId });
      return { operation: active, etag: null };
    });
    expect(await tracker.execute(intent, new AbortController().signal)).toEqual({ success: true });
    expect(getRandomValues).toHaveBeenCalledTimes(1);
    expect(localStorage.length).toBe(0);
  });

  it('does not send without a secure random source', async () => {
    vi.stubGlobal('crypto', {});
    await tracker.refresh();
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow('no cryptographically secure random source');
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });

  it('rejects stale-session callbacks and submissions', async () => {
    await tracker.refresh();
    sessionCurrent = false;
    await expect(tracker.refresh()).rejects.toThrow('session changed');
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow('session changed');
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });

  it.each([403, 404, 500])('requires an available current status after HTTP %s, but a fresh read unblocks', async statusCode => {
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockRejectedValueOnce({ statusCode });
    await expect(tracker.refresh()).rejects.toEqual({ statusCode });
    expect(tracker.isBlocked()).toBe(true);
    await tracker.refresh();
    expect(tracker.isBlocked()).toBe(false);
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });
});
