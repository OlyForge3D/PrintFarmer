import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import { isControlOperationResolved } from '@/types/api';
import type { PrinterControlOperation, PrinterControlOperationState } from '@/types/api';

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
const states: PrinterControlOperationState[] = ['Queued', 'Running', 'Unknown', 'Recovering', 'Succeeded', 'Failed', 'Recovered'];
function operation(state: PrinterControlOperationState): PrinterControlOperation {
  const settled = !['Queued', 'Running'].includes(state);
  return {
    printerId, operationId, kind: 'HomeAll', x: null, y: null, z: null, f: null, state,
    rowVersion: 'v1', createdAtUtc: '2026-09-12T18:00:00Z', updatedAtUtc: '2026-09-12T18:00:01Z',
    startedAtUtc: null, completedAtUtc: settled ? '2026-09-12T18:00:01Z' : null,
    barrierHeld: !settled, requiresRecovery: false,
    completionEvidence: state === 'Succeeded' ? 'MotionQueueDrained' : state === 'Failed' ? 'NotSent' : state === 'Recovered' ? 'OperatorVerifiedRecovery' : 'None',
    senderIsolation: 'NotRequested', failure: null,
  };
}
beforeEach(() => {
  vi.clearAllMocks();
  http.get.mockResolvedValue({ data: {}, headers: { etag: '"opaque+/revision="' } });
});

describe('motion API wire contract without recovery endpoints', () => {
  it.each([200, 202].flatMap(status => states.flatMap(state =>
    [false, true].map(barrierHeld => ({ status, state, barrierHeld })))))(
    'enforces HTTP $status for $state with barrierHeld=$barrierHeld', async ({ status, state, barrierHeld }) => {
      const data = { ...operation(state), barrierHeld };
      http.post.mockResolvedValueOnce({ status, data, headers: { etag: '"opaque+/revision="' } });
      const request = apiClient.createPrinterControlOperation(printerId, operationId, intent);
      if ((status === 200 && ['Succeeded', 'Failed', 'Unknown', 'Recovered'].includes(state)) ||
        (status === 202 && barrierHeld)) {
        const receipt = await request;
        expect(receipt).toEqual({ operation: data, etag: '"opaque+/revision="' });
        expect(receipt).not.toHaveProperty('success');
      } else {
        await expect(request).rejects.toThrow('Invalid durable motion admission receipt');
      }
      expect(http.post).toHaveBeenCalledExactlyOnceWith(`/printers/${printerId}/control-operations`, intent, { headers: { 'Idempotency-Key': operationId } });
      expect(http.get).not.toHaveBeenCalled();
    });

  it.each([
    { status: 202, state: 'Recovering' as const, completionEvidence: 'None' as const },
    { status: 200, state: 'Succeeded' as const, completionEvidence: 'MotionQueueDrained' as const },
    { status: 200, state: 'Succeeded' as const, completionEvidence: 'None' as const },
  ])('accepts held $state HTTP$status/$completionEvidence without releasing or claiming success', async ({ status, state, completionEvidence }) => {
    const data = { ...operation(state), barrierHeld: true, completionEvidence };
    http.post.mockResolvedValueOnce({ status, data, headers: {} });
    const receipt = await apiClient.createPrinterControlOperation(printerId, operationId, intent);
    expect(receipt.operation).toEqual(data);
    expect(receipt.operation.barrierHeld).toBe(true);
    expect(isControlOperationResolved(receipt.operation)).toBe(false);
    expect(receipt).not.toHaveProperty('success');
    expect(http.post).toHaveBeenCalledExactlyOnceWith(`/printers/${printerId}/control-operations`, intent, { headers: { 'Idempotency-Key': operationId } });
    expect(http.get).not.toHaveBeenCalled();
  });

  it.each([200, 202].flatMap(status => (['x', 'y', 'z', 'f'] as const).map(axis => ({ status, axis }))))(
    'rejects mismatched $axis intent at HTTP $status without retrying', async ({ status, axis }) => {
      const move = { kind: 'MoveTo' as const, x: 10, y: 20, z: 30, f: 100 };
      const data = { ...operation(status === 200 ? 'Succeeded' : 'Running'), ...move, [axis]: 99 };
      http.post.mockResolvedValue({ status, data, headers: {} });
      await expect(apiClient.createPrinterControlOperation(printerId, operationId, move)).rejects.toThrow();
      expect(http.post).toHaveBeenCalledTimes(1);
    });

  it.each([201, 203, 204, 205, 206, 207, 208, 226])('rejects unexpected HTTP %s', async status => {
    http.post.mockResolvedValueOnce({ status, data: operation('Running'), headers: {} });
    await expect(apiClient.createPrinterControlOperation(printerId, operationId, intent)).rejects.toThrow('Invalid durable motion admission receipt');
  });

  it.each([
    { barrierHeld: 'true' }, { operationId: '33333333-3333-4333-8333-333333333333' },
    { printerId: '33333333-3333-4333-8333-333333333333' }, { kind: 'HomeZ' },
    { state: 'UnsupportedState' }, { failure: undefined }, { x: undefined },
  ])('rejects malformed or mismatched settled receipt %j', async invalid => {
    http.post.mockResolvedValueOnce({ status: 200, data: { ...operation('Succeeded'), ...invalid }, headers: {} });
    await expect(apiClient.createPrinterControlOperation(printerId, operationId, intent)).rejects.toThrow();
  });

  it('allows historical recovery evidence without treating it as motion success', async () => {
    const data = { ...operation('Recovered'), requiresRecovery: true, senderIsolation: 'Pending' };
    http.post.mockResolvedValueOnce({ status: 200, data, headers: {} });
    const receipt = await apiClient.createPrinterControlOperation(printerId, operationId, intent);
    expect(receipt.operation).toEqual(data);
    expect(receipt).not.toHaveProperty('success');
    expect(apiClient).not.toHaveProperty('recoverPrinterControlOperation');
  });

  it('reads current and exact operation with no-cache REST', async () => {
    await apiClient.getCurrentPrinterControlOperation('printer');
    const result = await apiClient.getPrinterControlOperation('printer', 'operation');
    expect(http.get).toHaveBeenNthCalledWith(1, '/printers/printer/control-operations/current', { headers: { 'Cache-Control': 'no-cache' } });
    expect(http.get).toHaveBeenNthCalledWith(2, '/printers/printer/control-operations/operation', { headers: { 'Cache-Control': 'no-cache' } });
    expect(result.etag).toBe('"opaque+/revision="');
    expect(http.post).not.toHaveBeenCalled();
  });

  it('reads calibration status and configuration with no-cache REST', async () => {
    await apiClient.getPrinterStatus('printer');
    await apiClient.getPrinters(true, true);
    expect(http.get).toHaveBeenNthCalledWith(1, '/printers/printer/status', { headers: { 'Cache-Control': 'no-cache' } });
    expect(http.get).toHaveBeenNthCalledWith(2, '/printers', { params: { includeDisabled: true }, headers: { 'Cache-Control': 'no-cache' } });
  });
});
