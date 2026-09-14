import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { AuthContext } from '@/common/contexts/auth-context';
import type { AuthContextType } from '@/contexts/AuthContextValue';
import { usePrinterControlOperation } from '@/features/printers/hooks/use-printer-control-operation';
import { apiClient } from '@/services/api';
import { CONTROL_RECHECK_MS } from '@/services/printer-control-operations';
import { PrinterBackend, type PrinterControlIntent, type PrinterControlOperation } from '@/types/api';

const events = vi.hoisted(() => ({
  connected: false,
  hint: null as null | ((event: { printerId: string; operationId: string; rowVersion: string }) => void),
  connection: null as null | ((connected: boolean) => void),
}));
vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    get isConnected() { return events.connected; },
    subscribeToPrinter: vi.fn().mockResolvedValue(undefined),
    onControlOperationUpdated: vi.fn(callback => { events.hint = callback; return vi.fn(); }),
    onConnectionStateChange: vi.fn(callback => { events.connection = callback; return vi.fn(); }),
  },
}));
vi.mock('@/common/hooks/useApi', () => ({ queryKeys: { printers: ['printers'] } }));
vi.mock('@/services/api', () => ({
  apiClient: {
    homePrinter: vi.fn(), homeXY: vi.fn(), homeZ: vi.fn(), movePrinter: vi.fn(), movePrinterTo: vi.fn(),
    createPrinterControlOperation: vi.fn(), getCurrentPrinterControlOperation: vi.fn(), getPrinterControlOperation: vi.fn(),
  },
}));

const printerId = '11111111-1111-4111-8111-111111111111';
let op: PrinterControlOperation | null;
let auth: AuthContextType;
let identity = 0;
let client: QueryClient;
const intents: PrinterControlIntent[] = [
  { kind: 'HomeAll' }, { kind: 'HomeXY' }, { kind: 'HomeZ' },
  { kind: 'Jog', x: 5 }, { kind: 'MoveTo', x: 110, y: 110, z: 10, f: 3000 },
];
function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={client}><AuthContext.Provider value={auth}>{children}</AuthContext.Provider></QueryClientProvider>;
}
beforeEach(() => {
  vi.useFakeTimers();
  vi.clearAllMocks();
  events.connected = false;
  localStorage.clear();
  identity++;
  localStorage.setItem('auth-token', `session-${identity}`);
  auth = {
    user: { id: `subject-${identity}`, roles: [], permissions: [] },
    isAuthenticated: true, hasRole: () => false, hasPermission: () => false,
  } as unknown as AuthContextType;
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  op = null;
  vi.mocked(apiClient.getCurrentPrinterControlOperation).mockImplementation(async () => ({
    physicalControl: {
      supportedOperations: ['HomeAll', 'HomeXY', 'HomeZ', 'Jog', 'MoveTo'],
      barrierHeld: op?.barrierHeld ?? false,
      operationId: op?.barrierHeld ? op.operationId : null,
      state: op?.barrierHeld ? op.state : null,
      requiresRecovery: op?.requiresRecovery ?? false,
    }, operation: op?.barrierHeld ? op : null,
  }));
  vi.mocked(apiClient.getPrinterControlOperation).mockImplementation(async () => ({ operation: op!, etag: '"v1"' }));
  vi.mocked(apiClient.createPrinterControlOperation).mockImplementation(async (_printerId, operationId, intent) => {
    op = {
      printerId, operationId, kind: intent.kind, x: intent.x ?? null, y: intent.y ?? null, z: intent.z ?? null, f: intent.f ?? null,
      state: 'Running', rowVersion: 'v1', barrierHeld: true, requiresRecovery: false, completionEvidence: 'None',
      failure: null, senderIsolation: 'NotRequested',
      createdAtUtc: '2026-09-12T18:00:00Z', updatedAtUtc: '2026-09-12T18:00:00Z', startedAtUtc: null, completedAtUtc: null,
    };
    return { operation: op, etag: '"v1"' };
  });
  for (const fn of [apiClient.homePrinter, apiClient.homeXY, apiClient.homeZ, apiClient.movePrinter, apiClient.movePrinterTo]) {
    vi.mocked(fn).mockResolvedValue({ success: true });
  }
});
afterEach(() => { client.clear(); vi.useRealTimers(); });

describe('motion clients and invalidation lifecycle', () => {
  it('does not expose recovery permissions or admission retries to an ordinary operator', async () => {
    const { result, unmount } = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.Moonraker }), { wrapper });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(result.current.blocked).toBe(false);
    expect(result.current).not.toHaveProperty('canRecover');
    expect(result.current).not.toHaveProperty('canRetryAdmission');
    unmount();
  });

  it.each(intents)('Moonraker $kind uses only durable admission and REST completion', async intent => {
    const { result, unmount } = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.Moonraker }), { wrapper });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(result.current.blocked).toBe(false);
    let task!: ReturnType<typeof result.current.execute>;
    await act(async () => { task = result.current.execute(intent); await vi.advanceTimersByTimeAsync(0); });
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledWith(printerId, expect.any(String), intent);
    expect(result.current.blocked).toBe(true);
    op = { ...op!, state: 'Succeeded', barrierHeld: false, completionEvidence: 'MotionQueueDrained' };
    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    expect((await task).success).toBe(true);
    for (const fn of [apiClient.homePrinter, apiClient.homeXY, apiClient.homeZ, apiClient.movePrinter, apiClient.movePrinterTo]) expect(fn).not.toHaveBeenCalled();
    unmount();
  });

  it('preserves all five non-Moonraker legacy routes without durable admission', async () => {
    const { result, unmount } = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.PrusaLink }), { wrapper });
    for (const intent of intents) expect((await result.current.execute(intent)).success).toBe(true);
    expect(apiClient.homePrinter).toHaveBeenCalledWith(printerId);
    expect(apiClient.homeXY).toHaveBeenCalledWith(printerId);
    expect(apiClient.homeZ).toHaveBeenCalledWith(printerId);
    expect(apiClient.movePrinter).toHaveBeenCalledWith(printerId, { x: 5 });
    expect(apiClient.movePrinterTo).toHaveBeenCalledWith(printerId, { x: 110, y: 110, z: 10, f: 3000 });
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
    unmount();
  });

  it('polls connected SignalR without invalidations, coalesces mounted views, and stops after terminal/unmount', async () => {
    events.connected = true;
    const first = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.Moonraker }), { wrapper });
    const second = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.Moonraker }), { wrapper });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    let task!: ReturnType<typeof first.result.current.execute>;
    await act(async () => { task = first.result.current.execute(intents[0]); await vi.advanceTimersByTimeAsync(0); });
    const initialReads = vi.mocked(apiClient.getPrinterControlOperation).mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
    expect(apiClient.getPrinterControlOperation).toHaveBeenCalledTimes(initialReads + 10_000 / CONTROL_RECHECK_MS);
    expect(first.result.current.blocked).toBe(true);
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
    op = { ...op!, state: 'Succeeded', completionEvidence: 'MotionQueueDrained', barrierHeld: false };
    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    expect((await task).success).toBe(true);
    const finalReads = vi.mocked(apiClient.getCurrentPrinterControlOperation).mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(6_000); });
    expect(apiClient.getCurrentPrinterControlOperation).toHaveBeenCalledTimes(finalReads);
    first.unmount(); second.unmount();
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
    expect(apiClient.getCurrentPrinterControlOperation).toHaveBeenCalledTimes(finalReads);
  });

  it('releases missing admission after a fresh idle status, without polling forever or resubmitting', async () => {
    events.connected = true;
    vi.mocked(apiClient.createPrinterControlOperation).mockRejectedValueOnce(new Error('lost before admission'));
    vi.mocked(apiClient.getPrinterControlOperation).mockRejectedValue({ statusCode: 404 });
    const view = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.Moonraker }), { wrapper });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    await act(async () => { await expect(view.result.current.execute(intents[0])).rejects.toThrow('no command was retried'); });
    const reads = vi.mocked(apiClient.getPrinterControlOperation).mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(6_000); });
    expect(apiClient.getPrinterControlOperation).toHaveBeenCalledTimes(reads);
    expect(view.result.current.blocked).toBe(false);
    expect(view.result.current.error).toContain('outcome is unknown');
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
    view.unmount();
    await act(async () => { await vi.advanceTimersByTimeAsync(6_000); });
    expect(apiClient.getPrinterControlOperation).toHaveBeenCalledTimes(reads);
  });

  it('settles Unknown with honest failure feedback and makes ordinary motion available again', async () => {
    const { result, unmount } = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.Moonraker }), { wrapper });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    let task!: ReturnType<typeof result.current.execute>;
    await act(async () => { task = result.current.execute(intents[0]); await vi.advanceTimersByTimeAsync(0); });
    op = { ...op!, state: 'Unknown', barrierHeld: false, senderIsolation: 'Pending' };
    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    expect(await task).toMatchObject({ success: false, error: expect.stringContaining('Unknown') });
    expect(result.current.blocked).toBe(false);
    expect(result.current.saved).toBeNull();
    expect(result.current.operation?.state).toBe('Unknown');
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
    expect([...Array(localStorage.length)].map((_, index) => localStorage.key(index))).toEqual(['auth-token']);
    unmount();
  });

  it('rechecks REST on reconnect, foreground, navigation and stale/duplicate hints without replay', async () => {
    const view = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.Moonraker }), { wrapper });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    await act(async () => { events.connection?.(true); await vi.advanceTimersByTimeAsync(0); });
    await act(async () => { window.dispatchEvent(new Event('focus')); await vi.advanceTimersByTimeAsync(0); });
    await act(async () => {
      events.hint?.({ printerId, operationId: 'stale-id', rowVersion: 'old' });
      events.hint?.({ printerId, operationId: 'stale-id', rowVersion: 'old' });
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(view.result.current.operation).toBeNull();
    expect(apiClient.getCurrentPrinterControlOperation).toHaveBeenCalledTimes(5);
    view.unmount();
    const reopened = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.Moonraker }), { wrapper });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(apiClient.getCurrentPrinterControlOperation).toHaveBeenCalledTimes(6);
    expect(apiClient.createPrinterControlOperation).not.toHaveBeenCalled();
    reopened.unmount();
  });

  it('keeps pending admission when unmounted and does not expose it to another subject', async () => {
    const first = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.Moonraker }), { wrapper });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    let waiting!: Promise<unknown>;
    await act(async () => { waiting = first.result.current.execute(intents[0]).catch(error => error); await vi.advanceTimersByTimeAsync(0); });
    const savedId = first.result.current.saved?.operationId;
    first.unmount();
    await waiting;
    const reopened = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.Moonraker }), { wrapper });
    expect(reopened.result.current.saved?.operationId).toBe(savedId);
    reopened.unmount();
    localStorage.setItem('auth-token', 'another-session');
    auth = { ...auth, user: { ...auth.user!, id: 'another-subject' } };
    op = null;
    const another = renderHook(() => usePrinterControlOperation({ id: printerId, backend: PrinterBackend.Moonraker }), { wrapper });
    expect(another.result.current.saved).toBeNull();
    expect(another.result.current.operation).toBeNull();
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(apiClient.createPrinterControlOperation).toHaveBeenCalledTimes(1);
    another.unmount();
  });
});
