import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import { PrinterControlTracker } from '@/services/printer-control-operations';
import type { PrinterControlOperation, PrinterControlOperationState, PrinterControlRecovery } from '@/types/api';

const http = vi.hoisted(() => ({
  get: vi.fn(), post: vi.fn(),
  interceptors: { request: { use: vi.fn() }, response: { use: vi.fn() } },
}));
vi.mock('axios', async () => {
  const actual = await vi.importActual<typeof import('axios')>('axios');
  return { default: { ...actual.default, create: vi.fn(() => http) } };
});
const printerId = '11111111-1111-4111-8111-111111111111';
const operationId = '22222222-2222-4222-8222-222222222222';
const intent = { kind: 'HomeAll' as const };
const storageKey = 'admission-wire-server-actor-printer';
const states: PrinterControlOperationState[] = ['Queued', 'Running', 'Unknown', 'Recovering', 'Succeeded', 'Failed', 'Recovered'];
function operation(state: PrinterControlOperationState): PrinterControlOperation {
  const terminal = ['Succeeded', 'Failed', 'Recovered'].includes(state);
  return {
    printerId, operationId, kind: 'HomeAll', x: null, y: null, z: null, f: null, state,
    rowVersion: 'v1', createdAtUtc: '2026-09-12T18:00:00Z', updatedAtUtc: '2026-09-12T18:00:01Z',
    startedAtUtc: null, completedAtUtc: terminal ? '2026-09-12T18:00:01Z' : null,
    barrierHeld: !terminal, requiresRecovery: state === 'Unknown' || state === 'Recovering',
    completionEvidence: state === 'Succeeded' ? 'MotionQueueDrained' : state === 'Failed' ? 'NotSent' : state === 'Recovered' ? 'OperatorVerifiedRecovery' : 'None',
    senderIsolation: 'NotRequested', failure: null,
  };
}
function unresolvedReads() {
  http.get.mockImplementation(async (url: string) => {
    if (!url.endsWith('/current')) throw { statusCode: 404 };
    return { data: {
      physicalControl: { supportedOperations: ['HomeAll', 'MoveTo'], barrierHeld: false, requiresRecovery: false, operationId: null, state: null },
      operation: null,
    }, headers: {} };
  });
}
beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  http.get.mockResolvedValue({ data: { operationId: 'operation' }, headers: { etag: '"opaque+/revision="' } });
  http.post.mockResolvedValue({ status: 202, data: { operationId: 'operation' }, headers: { location: '/operation', etag: '"opaque+/revision="' } });
});
afterEach(() => vi.restoreAllMocks());
describe('durable motion API wire contract', () => {
  it.each([200, 202].flatMap(status => (['x', 'y', 'z', 'f'] as const).map(axis => ({ status, axis }))))(
    'rejects HTTP$status $axis intent mismatch without confirming or clearing the saved admission', async ({ status, axis }) => {
      const move = { kind: 'MoveTo' as const, x: 10, y: 20, z: 30, f: 100 };
      const data = { ...operation(status === 200 ? 'Succeeded' : 'Running'), ...move, [axis]: 90 };
      unresolvedReads();
      vi.spyOn(crypto, 'randomUUID').mockReturnValueOnce(operationId);
      http.post.mockResolvedValue({ status, data, headers: {} });
      const tracker = new PrinterControlTracker(printerId, storageKey, () => true);
      await tracker.refresh();
      await expect(tracker.execute(move, new AbortController().signal)).rejects.toThrow('retained');
      expect(JSON.parse(localStorage.getItem(storageKey)!)).toEqual({ operationId, intent: move });
      expect(tracker.getSnapshot().saved?.admissionConfirmed).not.toBe(true);
      expect(tracker.getCompletedOperation()).toBeNull();
      expect(tracker.getSnapshot().uncertain).toBe(true);
      expect(tracker.isBlocked()).toBe(true);
      expect(http.post).toHaveBeenCalledExactlyOnceWith(`/printers/${printerId}/control-operations`, move, { headers: { 'Idempotency-Key': operationId } });
    });
  it.each([200, 202])('accepts matching complete intent and omitted/null coordinates with HTTP%s', async status => {
    const move = { kind: 'MoveTo' as const, x: 10, y: 20, z: 30, f: 100 };
    const data = { ...operation(status === 200 ? 'Succeeded' : 'Running'), ...move };
    http.post.mockResolvedValueOnce({ status, data, headers: {} });
    expect((await apiClient.createPrinterControlOperation(printerId, operationId, move)).operation).toEqual(data);
    const homing = operation(status === 200 ? 'Succeeded' : 'Running');
    http.post.mockResolvedValueOnce({ status, data: homing, headers: {} });
    expect((await apiClient.createPrinterControlOperation(printerId, operationId, intent)).operation).toEqual(homing);
    expect(intent).toEqual({ kind: 'HomeAll' });
  });
  it.each([200, 202].flatMap(status => states.map(state => ({ status, state }))))('enforces admission HTTP $status with $state receipt', async ({ status, state }) => {
    const data = operation(state);
    http.post.mockResolvedValueOnce({ status, data, headers: { etag: '"opaque+/revision="' } });
    const request = apiClient.createPrinterControlOperation(printerId, operationId, intent);
    const terminal = ['Succeeded', 'Failed', 'Recovered'].includes(state);
    if ((status === 200) === terminal) {
      const receipt = await request;
      expect(receipt).toEqual({ operation: data, etag: '"opaque+/revision="' });
      expect(receipt).not.toHaveProperty('success');
    } else {
      await expect(request).rejects.toThrow('Invalid durable motion admission receipt');
    }
    expect(http.post).toHaveBeenCalledExactlyOnceWith(`/printers/${printerId}/control-operations`, intent, { headers: { 'Idempotency-Key': operationId } });
    expect(http.get).not.toHaveBeenCalled();
  });
  it.each([201, 203, 204, 205, 206, 207, 208, 226])('rejects unexpected successful HTTP %s instead of inferring admission from shape', async status => {
    http.post.mockResolvedValueOnce({ status, data: operation('Running'), headers: {} });
    await expect(apiClient.createPrinterControlOperation(printerId, operationId, intent)).rejects.toThrow('Invalid durable motion admission receipt');
  });
  it.each([
    { completionEvidence: 'None' }, { completionEvidence: 'NotSent' }, { barrierHeld: true }, { requiresRecovery: true },
    { operationId: '33333333-3333-4333-8333-333333333333' }, { printerId: '33333333-3333-4333-8333-333333333333' },
    { kind: 'HomeZ' }, { state: 'UnsupportedState' }, { failure: undefined }, { x: undefined },
  ])('rejects malformed or mismatched terminal HTTP200 receipt: %j', async invalid => {
    http.post.mockResolvedValueOnce({ status: 200, data: { ...operation('Succeeded'), ...invalid }, headers: {} });
    await expect(apiClient.createPrinterControlOperation(printerId, operationId, intent)).rejects.toThrow();
  });
  it('accepts contract-allowed BackendRejected terminal failure without calling it physical success', async () => {
    const data = { ...operation('Failed'), completionEvidence: 'BackendRejected' };
    http.post.mockResolvedValueOnce({ status: 200, data, headers: {} });
    const receipt = await apiClient.createPrinterControlOperation(printerId, operationId, intent);
    expect(receipt.operation.completionEvidence).toBe('BackendRejected');
    expect(receipt).not.toHaveProperty('success');
  });
  it.each([
    { status: 200, state: 'Running' as const }, { status: 202, state: 'Succeeded' as const },
    { status: 201, state: 'Queued' as const }, { status: 202, state: 'Running' as const, malformed: true },
  ])('retains the journal without admission confirmation after invalid POST $status/$state', async ({ status, state, malformed }) => {
    unresolvedReads();
    vi.spyOn(crypto, 'randomUUID').mockReturnValueOnce(operationId);
    http.post.mockResolvedValueOnce({ status, data: malformed ? { operationId } : operation(state), headers: {} });
    const tracker = new PrinterControlTracker(printerId, storageKey, () => true);
    await tracker.refresh();
    await expect(tracker.execute(intent, new AbortController().signal)).rejects.toThrow('retained');
    expect(JSON.parse(localStorage.getItem(storageKey)!)).toEqual({ operationId, intent });
    expect(tracker.getSnapshot().saved?.admissionConfirmed).not.toBe(true);
    expect(tracker.isBlocked()).toBe(true);
    await expect(tracker.refresh()).rejects.toEqual({ statusCode: 404 });
    expect(http.post).toHaveBeenCalledTimes(1);
  });
  it.each([intent, { kind: 'MoveTo' as const, x: 10, y: 20, z: 30, f: 100 }])('handles matching %j terminal200 replay but waits for canonical GET/current before clearing', async originalIntent => {
    const receipt = { ...operation('Succeeded'), ...originalIntent };
    unresolvedReads();
    localStorage.setItem(storageKey, JSON.stringify({ operationId, intent: originalIntent }));
    const tracker = new PrinterControlTracker(printerId, storageKey, () => true);
    await expect(tracker.refresh()).rejects.toEqual({ statusCode: 404 });
    http.post.mockResolvedValueOnce({ status: 200, data: receipt, headers: {} });
    await tracker.retryAdmission();
    expect(tracker.getSnapshot().saved?.admissionConfirmed).toBe(true);
    expect(tracker.isBlocked()).toBe(true);
    const currentRead = http.get.getMockImplementation()!;
    http.get.mockImplementation(async (url: string) => url.endsWith('/current')
      ? currentRead(url) : { data: receipt, headers: { etag: '"canonical"' } });
    await tracker.refresh();
    expect(tracker.getSnapshot().saved).toBeNull();
    expect(tracker.isBlocked()).toBe(false);
    expect(http.post).toHaveBeenCalledExactlyOnceWith(`/printers/${printerId}/control-operations`, originalIntent, { headers: { 'Idempotency-Key': operationId } });
  });
  it('reads current and exact operation through no-cache REST without reencoding the ETag', async () => {
    await apiClient.getCurrentPrinterControlOperation('printer');
    const result = await apiClient.getPrinterControlOperation('printer', 'operation');
    expect(http.get).toHaveBeenNthCalledWith(1, '/printers/printer/control-operations/current', { headers: { 'Cache-Control': 'no-cache' } });
    expect(http.get).toHaveBeenNthCalledWith(2, '/printers/printer/control-operations/operation', { headers: { 'Cache-Control': 'no-cache' } });
    expect(result.etag).toBe('"opaque+/revision="');
  });
  it('reads calibration status and enabled/maintenance configuration from their actual no-cache endpoints', async () => {
    await apiClient.getPrinterStatus('printer');
    await apiClient.getPrinters(true, true);
    expect(http.get).toHaveBeenNthCalledWith(1, '/printers/printer/status', { headers: { 'Cache-Control': 'no-cache' } });
    expect(http.get).toHaveBeenNthCalledWith(2, '/printers', { params: { includeDisabled: true }, headers: { 'Cache-Control': 'no-cache' } });
  });
  it('sends exact If-Match with separate recovery initiation/completion routes', async () => {
    const body: PrinterControlRecovery = {
      reason: 'reviewed', senderIsolation: 'ExternallyVerified', senderIsolationEvidence: 'sender isolated',
      controllerQueueCleared: true, physicallyStationary: true, physicalEvidence: 'queue cleared and stationary',
    };
    await apiClient.recoverPrinterControlOperation('printer', 'operation', '"opaque+/revision="');
    await apiClient.recoverPrinterControlOperation('printer', 'operation', '"opaque+/revision="', body);
    expect(http.post).toHaveBeenNthCalledWith(1, '/printers/printer/control-operations/operation/recovery', {}, { headers: { 'If-Match': '"opaque+/revision="' } });
    expect(http.post).toHaveBeenNthCalledWith(2, '/printers/printer/control-operations/operation/recovery/complete', body, { headers: { 'If-Match': '"opaque+/revision="' } });
  });
  it.each(['', 'bare-row-version', '*', 'W/"weak"'])('refuses missing or guessed precondition %s', async etag => {
    await expect(apiClient.recoverPrinterControlOperation('printer', 'operation', etag)).rejects.toThrow('ETag');
    expect(http.post).not.toHaveBeenCalled();
  });
});
