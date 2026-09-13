import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import { PrinterControlTracker } from '@/services/printer-control-operations';
import type { PrinterControlCurrent, PrinterControlOperation, PrinterControlRecovery } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    createPrinterControlOperation: vi.fn(),
    getCurrentPrinterControlOperation: vi.fn(),
    getPrinterControlOperation: vi.fn(),
    recoverPrinterControlOperation: vi.fn(),
  },
}));

const printerId = '11111111-1111-4111-8111-111111111111';
const operationId = '22222222-2222-4222-8222-222222222222';
const successorId = '33333333-3333-4333-8333-333333333333';
const storageKey = 'motion-test-server-subject-printer';
const intent = { kind: 'HomeAll' as const };
const moveIntent = { kind: 'MoveTo' as const, x: 10, y: 20, z: 30, f: 100 };
const axes = ['x', 'y', 'z', 'f'] as const;
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
  vi.mocked(apiClient.getPrinterControlOperation).mockImplementation(async () => ({ operation: active!, etag: '"opaque-v1"' }));
  vi.mocked(apiClient.createPrinterControlOperation).mockImplementation(async () => {
    expect(JSON.parse(localStorage.getItem(storageKey)!)).toEqual({ operationId, intent });
    active = operation();
    return { operation: active, etag: '"opaque-v1"' };
  });
  vi.mocked(apiClient.recoverPrinterControlOperation).mockImplementation(async () => ({ operation: active!, etag: '"opaque-v1"' }));
  tracker = new PrinterControlTracker(printerId, storageKey, () => sessionCurrent);
});
afterEach(() => { vi.restoreAllMocks(); vi.useRealTimers(); });

describe('durable motion tracking', () => {
  function restoreMove() {
    localStorage.setItem(storageKey, JSON.stringify({ operationId, intent: moveIntent }));
    tracker = new PrinterControlTracker(printerId, storageKey, () => sessionCurrent);
  }
  function expectUnconfirmedMove() {
    expect(JSON.parse(localStorage.getItem(storageKey)!)).toEqual({ operationId, intent: moveIntent });
    expect(tracker.getSnapshot().saved?.admissionConfirmed).not.toBe(true);
    expect(tracker.getCompletedOperation()).toBeNull();
    expect(tracker.getSnapshot().uncertain).toBe(true);
    expect(tracker.isBlocked()).toBe(true);
  }

  it.each((['exact', 'current'] as const).flatMap(source =>
    (['Running', 'Succeeded', 'Failed', 'Recovered'] as const).flatMap(state =>
      axes.map(axis => ({ source, state, axis })))))('rejects restored $source $state receipt with mismatched $axis without attributing admission or completion', async ({ source, state, axis }) => {
    restoreMove();
    const terminal = state !== 'Running';
    const receipt = operation({
      ...moveIntent, [axis]: 90, state, barrierHeld: !terminal,
      completionEvidence: state === 'Succeeded' ? 'MotionQueueDrained' : state === 'Failed' ? 'NotSent' : state === 'Recovered' ? 'OperatorVerifiedRecovery' : 'None',
    });
    if (source === 'current') {
      vi.mocked(apiClient.getCurrentPrinterControlOperation).mockResolvedValue({
        ...current(receipt), operation: receipt,
      });
    } else {
      vi.mocked(apiClient.getPrinterControlOperation).mockResolvedValue({ operation: receipt, etag: '"mismatch"' });
    }
    await expect(tracker.refresh()).rejects.toThrow('saved motion intent');
    expectUnconfirmedMove();
    expect(tracker.canRetryAdmission()).toBe(false);
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
    expect(apiClient.recoverPrinterControlOperation).not.toHaveBeenCalled();
  });

  it.each(axes)('rechecks %s binding in the final current read before clearing a matching terminal GET', async axis => {
    restoreMove();
    vi.mocked(apiClient.getPrinterControlOperation).mockResolvedValue({ operation: operation({ ...completed(), ...moveIntent }), etag: '"terminal"' });
    vi.mocked(apiClient.getCurrentPrinterControlOperation)
      .mockResolvedValueOnce(current())
      .mockResolvedValueOnce(current(operation({ ...moveIntent, [axis]: 90 })));
    await expect(tracker.refresh()).rejects.toThrow('saved motion intent');
    expectUnconfirmedMove();
  });

  it.each(axes)('does not confirm matching current when exact GET contradicts its %s intent', async axis => {
    restoreMove();
    active = operation(moveIntent);
    vi.mocked(apiClient.getPrinterControlOperation).mockResolvedValue({ operation: operation({ ...completed(), ...moveIntent, [axis]: 90 }), etag: '"wrong"' });
    await expect(tracker.refresh()).rejects.toThrow('saved motion intent');
    expectUnconfirmedMove();
  });

  it.each(axes)('independently checks new submission receipt %s intent when the transport is mocked', async axis => {
    await tracker.refresh();
    vi.mocked(apiClient.createPrinterControlOperation).mockImplementationOnce(async () => {
      active = operation({ ...moveIntent, [axis]: 90 });
      return { operation: active, etag: '"wrong"' };
    });
    await expect(tracker.execute(moveIntent, new AbortController().signal)).rejects.toThrow('retained');
    expectUnconfirmedMove();
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledExactlyOnceWith(printerId, operationId, moveIntent);
  });

  it.each(axes)('retains the original %s intent after explicit readmission returns another intent', async axis => {
    restoreMove();
    vi.mocked(apiClient.getPrinterControlOperation).mockRejectedValue({ statusCode: 404 });
    await expect(tracker.refresh()).rejects.toEqual({ statusCode: 404 });
    vi.mocked(apiClient.createPrinterControlOperation).mockImplementationOnce(async () => {
      active = operation({ ...moveIntent, [axis]: 90 });
      return { operation: active, etag: '"wrong"' };
    });
    await expect(tracker.retryAdmission()).rejects.toThrow('saved motion intent');
    expectUnconfirmedMove();
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledExactlyOnceWith(printerId, operationId, moveIntent);
    expect(crypto.randomUUID).not.toHaveBeenCalled();
  });

  it.each([false, true].flatMap(complete => axes.map(axis => ({ complete, axis }))))(
    'rejects recovery complete=$complete receipt with mismatched $axis and preserves the journal', async ({ complete, axis }) => {
      restoreMove();
      active = operation({ ...moveIntent, state: 'Unknown', requiresRecovery: true });
      await tracker.refresh();
      const saved = localStorage.getItem(storageKey);
      const recovery: PrinterControlRecovery | undefined = complete ? {
        reason: 'Operator review', senderIsolation: 'ExternallyVerified', senderIsolationEvidence: 'Sender isolated',
        controllerQueueCleared: true, physicallyStationary: true, physicalEvidence: 'Queue cleared and printer stationary',
      } : undefined;
      vi.mocked(apiClient.recoverPrinterControlOperation).mockResolvedValueOnce({
        operation: operation({ ...moveIntent, [axis]: 90, state: complete ? 'Recovered' : 'Recovering',
          requiresRecovery: !complete, barrierHeld: !complete,
          completionEvidence: complete ? 'OperatorVerifiedRecovery' : 'None' }),
        etag: '"wrong"',
      });
      const reads = vi.mocked(apiClient.getPrinterControlOperation).mock.calls.length;
      await expect(tracker.recover(operationId, '"opaque-v1"', recovery)).rejects.toThrow('saved motion intent');
      expect(localStorage.getItem(storageKey)).toBe(saved);
      expect(tracker.getCompletedOperation()).toBeNull();
      expect(tracker.getSnapshot().uncertain).toBe(true);
      expect(tracker.isBlocked()).toBe(true);
      expect(apiClient.getPrinterControlOperation).toHaveBeenCalledTimes(reads);
      expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
    });

  it('accepts matching restored full intent through current/exact reads and terminal completion', async () => {
    restoreMove();
    active = operation(moveIntent);
    await tracker.refresh();
    expect(tracker.getSnapshot().saved).toEqual({ operationId, intent: moveIntent, admissionConfirmed: true });
    expect(tracker.isBlocked()).toBe(true);
    active = operation({ ...completed(), ...moveIntent });
    await tracker.refresh();
    expect(localStorage.getItem(storageKey)).toBeNull();
    expect(tracker.getCompletedOperation()).toEqual(active);
    expect(tracker.isBlocked()).toBe(false);
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });

  it('displays a server-discovered operation with coordinates without inventing local expected intent', async () => {
    active = operation({ ...moveIntent, x: 90 });
    await tracker.refresh();
    expect(tracker.getSnapshot().operation).toEqual(active);
    expect(tracker.getSnapshot().uncertain).toBe(false);
    expect(tracker.getSnapshot().saved).toBeNull();
    expect(tracker.isBlocked()).toBe(true);
    expect(localStorage.getItem(storageKey)).toBeNull();
  });

  it('retains the barrier after 27 seconds and resolves only an authoritative terminal receipt', async () => {
    await tracker.refresh();
    const controller = new AbortController();
    let settled = false;
    const task = tracker.execute(intent, controller.signal).then(result => { settled = true; return result; });
    await vi.advanceTimersByTimeAsync(27_110);
    expect(settled).toBe(false);
    expect(tracker.isBlocked()).toBe(true);
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
    active = completed();
    await vi.advanceTimersByTimeAsync(2_000);
    expect(await task).toEqual({ success: true, error: undefined });
    expect(localStorage.getItem(storageKey)).toBeNull();
    expect(tracker.isBlocked()).toBe(false);
  });

  it('never treats an HTTP admission body as success even if it claims Succeeded', async () => {
    await tracker.refresh();
    vi.mocked(apiClient.createPrinterControlOperation).mockImplementation(async () => {
      active = operation();
      return { operation: completed(), etag: '"v2"' };
    });
    const controller = new AbortController();
    const task = tracker.execute(intent, controller.signal).catch(error => error);
    await vi.advanceTimersByTimeAsync(0);
    expect(tracker.getSnapshot().operation?.state).toBe('Running');
    expect(tracker.isBlocked()).toBe(true);
    controller.abort();
    expect(await task).toBeInstanceOf(Error);
    expect(localStorage.getItem(storageKey)).not.toBeNull();
  });

  it('polls authoritative REST without hints and stops its wait timer after terminal completion', async () => {
    await tracker.refresh();
    const task = tracker.execute(intent, new AbortController().signal);
    await vi.advanceTimersByTimeAsync(0);
    const readCount = vi.mocked(apiClient.getPrinterControlOperation).mock.calls.length;
    await vi.advanceTimersByTimeAsync(27_110);
    expect(apiClient.getPrinterControlOperation).toHaveBeenCalledTimes(readCount + 13);
    expect(tracker.isBlocked()).toBe(true);
    active = completed();
    await vi.advanceTimersByTimeAsync(2_000);
    expect((await task).success).toBe(true);
    const terminalReads = vi.mocked(apiClient.getPrinterControlOperation).mock.calls.length;
    await vi.advanceTimersByTimeAsync(10_000);
    expect(apiClient.getPrinterControlOperation).toHaveBeenCalledTimes(terminalReads);
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
  });

  it('coalesces timer polls during slow reads without queuing an immediate extra request', async () => {
    await tracker.refresh();
    await vi.advanceTimersByTimeAsync(2_000);
    let resolve!: (value: PrinterControlCurrent) => void;
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockReturnValueOnce(new Promise(r => { resolve = r; }));
    const first = tracker.poll();
    await vi.advanceTimersByTimeAsync(8_000);
    const second = tracker.poll();
    const third = tracker.poll();
    expect(apiClient.getCurrentPrinterControlOperation).toHaveBeenCalledTimes(2);
    resolve(current());
    await Promise.all([first, second, third]);
    await tracker.poll();
    expect(apiClient.getCurrentPrinterControlOperation).toHaveBeenCalledTimes(2);
  });

  it('persists a lost POST response across remount and explicitly retries only its saved UUID and intent', async () => {
    await tracker.refresh();
    vi.mocked(apiClient.createPrinterControlOperation).mockRejectedValueOnce(new Error('connection lost'));
    vi.mocked(apiClient.getPrinterControlOperation).mockRejectedValueOnce({ statusCode: 404 });
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow('retained');
    const saved = JSON.parse(localStorage.getItem(storageKey)!);
    expect(saved).toEqual({ operationId, intent });
    expect(tracker.isBlocked()).toBe(true);
    tracker = new PrinterControlTracker(printerId, storageKey, () => true);
    vi.mocked(apiClient.getPrinterControlOperation).mockRejectedValueOnce({ statusCode: 404 });
    await expect(tracker.refresh()).rejects.toEqual({ statusCode: 404 });
    vi.mocked(apiClient.getPrinterControlOperation).mockRejectedValueOnce({ statusCode: 404 });
    await tracker.retryAdmission();
    expect(apiClient.createPrinterControlOperation).toHaveBeenNthCalledWith(2, printerId, operationId, intent);
    expect(tracker.getSnapshot().operation?.state).toBe('Running');
    expect(tracker.isBlocked()).toBe(true);
  });

  it('a lost response to an admitted operation only triggers reads, not physical replay', async () => {
    await tracker.refresh();
    vi.mocked(apiClient.createPrinterControlOperation).mockImplementationOnce(async () => {
      active = operation();
      throw new Error('lost response');
    });
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow('retained');
    await tracker.refresh();
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
    expect(tracker.getSnapshot().saved?.operationId).toBe(operationId);
    expect(tracker.getSnapshot().missingAdmission).toBe(false);
    expect(tracker.canRetryAdmission()).toBe(false);
    await expect(tracker.retryAdmission()).rejects.toThrow();
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
  });

  it('preserves every saved coordinate and UUID after lost-before-admission, remount, and deliberate retry', async () => {
    const move = { kind: 'MoveTo' as const, x: 101.25, y: 87.5, z: 10, f: 3000 };
    const original = { ...move };
    await tracker.refresh();
    vi.mocked(apiClient.createPrinterControlOperation).mockRejectedValueOnce(new Error('lost before admission'));
    vi.mocked(apiClient.getPrinterControlOperation).mockRejectedValue({ statusCode: 404 });
    await expect(tracker.execute(move, new AbortController().signal)).rejects.toThrow('retained');
    move.x = 999;
    tracker = new PrinterControlTracker(printerId, storageKey, () => sessionCurrent);
    await expect(tracker.refresh()).rejects.toEqual({ statusCode: 404 });
    await vi.advanceTimersByTimeAsync(90_000);
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
    expect(tracker.canRetryAdmission()).toBe(true);
    vi.mocked(apiClient.createPrinterControlOperation).mockImplementationOnce(async () => {
      active = operation({ ...original });
      vi.mocked(apiClient.getPrinterControlOperation).mockResolvedValue({ operation: active, etag: '"v2"' });
      return { operation: active, etag: '"v2"' };
    });
    await tracker.retryAdmission();
    expect(apiClient.createPrinterControlOperation).toHaveBeenLastCalledWith(printerId, operationId, original);
    expect(crypto.randomUUID).toHaveBeenCalledTimes(1);
    expect(tracker.isBlocked()).toBe(true);
  });

  it('a late terminal admission replay still requires canonical GET and current before clearing', async () => {
    localStorage.setItem(storageKey, JSON.stringify({ operationId, intent }));
    tracker = new PrinterControlTracker(printerId, storageKey, () => sessionCurrent);
    vi.mocked(apiClient.getPrinterControlOperation).mockRejectedValue({ statusCode: 404 });
    await expect(tracker.refresh()).rejects.toEqual({ statusCode: 404 });
    vi.mocked(apiClient.createPrinterControlOperation).mockResolvedValueOnce({ operation: completed(), etag: '"post-terminal"' });
    await tracker.retryAdmission();
    expect(tracker.isBlocked()).toBe(true);
    expect(tracker.getSnapshot().saved?.admissionConfirmed).toBe(true);
    expect(tracker.canRetryAdmission()).toBe(false);
    vi.mocked(apiClient.getPrinterControlOperation).mockResolvedValueOnce({ operation: completed(), etag: '"get-terminal"' });
    await tracker.refresh();
    expect(tracker.getSnapshot().saved).toBeNull();
    expect(tracker.isBlocked()).toBe(false);
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
  });

  it.each(['Unknown', 'Recovering'] as const)('never offers known %s admission for replay after a later 404 or remount', async state => {
    localStorage.setItem(storageKey, JSON.stringify({ operationId, intent }));
    tracker = new PrinterControlTracker(printerId, storageKey, () => sessionCurrent);
    active = operation({ state, requiresRecovery: true });
    await tracker.refresh();
    expect(JSON.parse(localStorage.getItem(storageKey)!).admissionConfirmed).toBe(true);
    active = null;
    tracker = new PrinterControlTracker(printerId, storageKey, () => sessionCurrent);
    vi.mocked(apiClient.getPrinterControlOperation).mockRejectedValue({ statusCode: 404 });
    await expect(tracker.refresh()).rejects.toEqual({ statusCode: 404 });
    expect(tracker.canRetryAdmission()).toBe(false);
    await expect(tracker.retryAdmission()).rejects.toThrow();
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });

  it.each(['actor', 'access', 'successor', 'saved-intent'] as const)('rechecks %s authority before deliberate admission retry', async change => {
    localStorage.setItem(storageKey, JSON.stringify({ operationId, intent }));
    tracker = new PrinterControlTracker(printerId, storageKey, () => sessionCurrent);
    vi.mocked(apiClient.getPrinterControlOperation).mockRejectedValue({ statusCode: 404 });
    await expect(tracker.refresh()).rejects.toEqual({ statusCode: 404 });
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockImplementationOnce(async () => {
      if (change === 'actor') sessionCurrent = false;
      if (change === 'access') throw { statusCode: 403 };
      if (change === 'saved-intent') localStorage.setItem(storageKey, JSON.stringify({ operationId, intent: { kind: 'Jog', z: 1 } }));
      return change === 'successor' ? current(operation({ operationId: successorId })) : current();
    });
    await expect(tracker.retryAdmission()).rejects.toBeDefined();
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
    expect(tracker.isBlocked()).toBe(true);
  });

  it.each(['Recovered', 'Failed'] as const)('%s is not successful execution', async state => {
    await tracker.refresh();
    const task = tracker.execute(intent, new AbortController().signal);
    await vi.advanceTimersByTimeAsync(0);
    active = operation({ state, barrierHeld: false, completionEvidence: state === 'Recovered' ? 'OperatorVerifiedRecovery' : 'BackendRejected' });
    await vi.advanceTimersByTimeAsync(2_000);
    expect((await task).success).toBe(false);
  });

  it('Unknown remains unresolved and cannot be cleared by time or local retry', async () => {
    active = operation({ state: 'Unknown', requiresRecovery: true });
    await tracker.refresh();
    await vi.advanceTimersByTimeAsync(90_000);
    expect(tracker.isBlocked()).toBe(true);
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow();
    await expect(tracker.retryAdmission()).rejects.toThrow();
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });

  it('rereads current after terminal and keeps a successor operation locked', async () => {
    localStorage.setItem(storageKey, JSON.stringify({ operationId, intent }));
    tracker = new PrinterControlTracker(printerId, storageKey, () => true);
    active = completed();
    vi.mocked(apiClient.getCurrentPrinterControlOperation)
      .mockResolvedValueOnce(current(active))
      .mockResolvedValueOnce(current(operation({ operationId: successorId })));
    vi.mocked(apiClient.getPrinterControlOperation).mockImplementation(async (_printer, id) => ({
      operation: id === operationId ? completed() : operation({ operationId: successorId }), etag: '"v2"',
    }));
    await tracker.refresh();
    expect(tracker.getSnapshot().operation?.operationId).toBe(successorId);
    expect(tracker.isBlocked()).toBe(true);
    expect(tracker.getSnapshot().saved).toBeNull();
  });

  it('coalesces duplicate/out-of-order invalidation hints and rereads instead of applying hint state', async () => {
    let resolve!: (value: PrinterControlCurrent) => void;
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockReturnValueOnce(new Promise(r => { resolve = r; }));
    const first = tracker.refresh();
    const second = tracker.refresh();
    active = operation({ operationId: successorId });
    resolve(current(null));
    await first;
    await second;
    await vi.advanceTimersByTimeAsync(0);
    expect(tracker.getSnapshot().operation?.operationId).toBe(successorId);
    expect(tracker.isBlocked()).toBe(true);
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });

  it.each([
    { statusCode: 404 }, { statusCode: 403 }, new Error('timeout'),
  ])('missing/unauthorized/unreachable operation status never unlocks saved motion: %j', async failure => {
    localStorage.setItem(storageKey, JSON.stringify({ operationId, intent }));
    tracker = new PrinterControlTracker(printerId, storageKey, () => true);
    vi.mocked(apiClient.getPrinterControlOperation).mockRejectedValue(failure);
    await expect(tracker.refresh()).rejects.toBe(failure);
    expect(tracker.isBlocked()).toBe(true);
    expect(localStorage.getItem(storageKey)).not.toBeNull();
  });

  it('invalid or incomplete current status fails closed, including unknown state values', async () => {
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockResolvedValue({
      ...current(operation()), operation: { ...operation(), state: 'Done' },
    } as unknown as PrinterControlCurrent);
    await expect(tracker.refresh()).rejects.toThrow();
    expect(tracker.isBlocked()).toBe(true);
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockResolvedValue({} as PrinterControlCurrent);
    await expect(tracker.refresh()).rejects.toThrow();
    expect(tracker.isBlocked()).toBe(true);
  });

  it('rejects a terminal non-owner returned by current instead of treating it as unlocked', async () => {
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockResolvedValue({
      physicalControl: {
        supportedOperations: ['HomeAll'], barrierHeld: false, requiresRecovery: false,
        operationId, state: 'Succeeded',
      },
      operation: completed(),
    });
    await expect(tracker.refresh()).rejects.toThrow('Inconsistent');
    expect(tracker.isBlocked()).toBe(true);
  });

  it('ambiguous 404 status fails closed without asserting the server needs an update', async () => {
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockRejectedValue({ statusCode: 404 });
    await expect(tracker.refresh()).rejects.toEqual({ statusCode: 404 });
    expect(tracker.getSnapshot().error).toContain('absent, inaccessible, or unsupported');
    expect(tracker.getSnapshot().error).not.toContain('Update the server');
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow();
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });

  it.each([405, 501])('unsupported status %s explains update requirement without admission', async statusCode => {
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockRejectedValue({ statusCode });
    await expect(tracker.refresh()).rejects.toEqual({ statusCode });
    expect(tracker.getSnapshot().error).toContain('Update the server');
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow();
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });

  it('accepts an unrelated held barrier without an operation and keeps motion blocked', async () => {
    const held = current();
    held.physicalControl.barrierHeld = true;
    held.physicalControl.requiresRecovery = true;
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockResolvedValueOnce(held);
    await tracker.refresh();
    expect(tracker.getSnapshot().uncertain).toBe(false);
    expect(tracker.getSnapshot().operation).toBeNull();
    expect(tracker.isBlocked()).toBe(true);
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow();
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });

  it('rechecks a transient current mismatch without permanently marking incompatibility', async () => {
    const mismatched = current();
    mismatched.operation = completed();
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockResolvedValueOnce(mismatched);
    await expect(tracker.refresh()).rejects.toThrow('Inconsistent');
    expect(tracker.isBlocked()).toBe(true);
    expect(tracker.getSnapshot().error).toContain('Recheck');
    await tracker.refresh();
    expect(tracker.getSnapshot().uncertain).toBe(false);
    expect(tracker.getSnapshot().error).toBeNull();
    expect(tracker.isBlocked()).toBe(false);
  });

  it('session changes suppress late responses and forbid another account reading or resending the receipt', async () => {
    let resolve!: (value: PrinterControlCurrent) => void;
    vi.mocked(apiClient.getCurrentPrinterControlOperation).mockReturnValueOnce(new Promise(r => { resolve = r; }));
    const read = tracker.refresh();
    sessionCurrent = false;
    resolve(current(operation()));
    await expect(read).rejects.toThrow('session changed');
    expect(tracker.getSnapshot().operation).toBeNull();
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow('session changed');
  });

  it('does not actuate when durable local receipt storage is unavailable', async () => {
    await tracker.refresh();
    vi.spyOn(localStorage, 'setItem').mockImplementation(() => { throw new Error('storage unavailable'); });
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow('storage unavailable');
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });

  it('does not overwrite a receipt reserved by another tab after this tab loaded status', async () => {
    await tracker.refresh();
    localStorage.setItem(storageKey, JSON.stringify({ operationId: successorId, intent: { kind: 'Jog', z: 1 } }));
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow('Another tab');
    expect(tracker.getSnapshot().saved?.operationId).toBe(successorId);
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
  });

  it('clearing a terminal receipt never deletes a successor receipt written by another tab', async () => {
    localStorage.setItem(storageKey, JSON.stringify({ operationId, intent }));
    tracker = new PrinterControlTracker(printerId, storageKey, () => true);
    active = completed();
    vi.mocked(apiClient.getPrinterControlOperation).mockImplementationOnce(async () => {
      localStorage.setItem(storageKey, JSON.stringify({ operationId: successorId, intent }));
      return { operation: completed(), etag: '"v2"' };
    });
    await tracker.refresh();
    expect(JSON.parse(localStorage.getItem(storageKey)!).operationId).toBe(successorId);
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow('Another tab');
  });

  it('recovery uses the exact reviewed ETag and rejects stale local review before any request', async () => {
    active = operation({ state: 'Unknown', requiresRecovery: true });
    await tracker.refresh();
    await expect(tracker.recover(operationId, '"old"')).rejects.toThrow('changed');
    expect(apiClient.recoverPrinterControlOperation).not.toHaveBeenCalled();
    await tracker.recover(operationId, '"opaque-v1"');
    expect(apiClient.recoverPrinterControlOperation).toHaveBeenCalledWith(printerId, operationId, '"opaque-v1"', undefined);
    expect(tracker.isBlocked()).toBe(true);
  });

  it.each([403, 409, 412, 428])('recovery HTTP %s rereads authoritative state and never clears the barrier', async statusCode => {
    active = operation({ state: 'Recovering', requiresRecovery: true });
    await tracker.refresh();
    vi.mocked(apiClient.recoverPrinterControlOperation).mockRejectedValueOnce({ statusCode });
    await expect(tracker.recover(operationId, '"opaque-v1"')).rejects.toEqual({ statusCode });
    expect(tracker.isBlocked()).toBe(true);
    expect(apiClient.getCurrentPrinterControlOperation).toHaveBeenCalledTimes(2);
  });
});
