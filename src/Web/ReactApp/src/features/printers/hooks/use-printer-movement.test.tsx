import type { PropsWithChildren } from 'react';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider, onlineManager } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import { usePrinterMovement, type PrinterMovement } from '@/features/printers/hooks/use-printer-movement';
import type { CommandResult } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: { homePrinter: vi.fn(), homeXY: vi.fn(), homeZ: vi.fn(), movePrinter: vi.fn(), movePrinterTo: vi.fn() },
}));
vi.mock('@/common/hooks/useApi', () => ({ queryKeys: { printers: ['printers'] } }));

function setup(printerId = 'printer-1') {
  const client = new QueryClient({ defaultOptions: { mutations: { retry: 3, retryDelay: 0 } } });
  const wrapper = ({ children }: PropsWithChildren) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  return { client, wrapper, ...renderHook(() => usePrinterMovement({ id: printerId }), { wrapper }) };
}

beforeEach(() => {
  vi.resetAllMocks();
  for (const command of Object.values(apiClient)) vi.mocked(command).mockResolvedValue({ success: true });
});
afterEach(() => onlineManager.setOnline(true));

describe('direct printer movement', () => {
  it.each<{ intent: PrinterMovement; method: keyof typeof apiClient; body?: object }>([
    { intent: { kind: 'HomeAll' }, method: 'homePrinter' },
    { intent: { kind: 'HomeXY' }, method: 'homeXY' },
    { intent: { kind: 'HomeZ' }, method: 'homeZ' },
    { intent: { kind: 'Jog', y: -10, f: 300 }, method: 'movePrinter', body: { y: -10, f: 300 } },
    { intent: { kind: 'MoveTo', x: 10, y: 20, z: 30 }, method: 'movePrinterTo', body: { x: 10, y: 20, z: 30 } },
    { intent: { kind: 'MoveTo', z: 9.95, f: 300 }, method: 'movePrinterTo', body: { z: 9.95, f: 300 } },
  ])('sends one $method request preserving its parameters', async ({ intent, method, body }) => {
    const { result, client } = setup();
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    expect(result.current.blocked).toBe(false);
    await act(async () => expect(await result.current.execute(intent)).toEqual({ success: true }));
    expect(apiClient[method]).toHaveBeenCalledExactlyOnceWith(...(body ? ['printer-1', body] : ['printer-1']));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['printers'] });
    expect(Object.values(apiClient).reduce((count, mock) => count + vi.mocked(mock).mock.calls.length, 0)).toBe(1);
  });

  it('shares pending state and prevents same-tick overlap across mounted controls', async () => {
    let finish!: (result: CommandResult) => void;
    vi.mocked(apiClient.homePrinter).mockImplementation(() => new Promise(resolve => { finish = resolve; }));
    const { result: first, wrapper } = setup();
    const second = renderHook(() => usePrinterMovement({ id: 'printer-1' }), { wrapper });
    const other = renderHook(() => usePrinterMovement({ id: 'printer-2' }), { wrapper });
    let pending!: Promise<CommandResult>;
    act(() => { pending = first.current.execute({ kind: 'HomeAll' }); });
    await waitFor(() => expect(first.current.blocked).toBe(true));
    await waitFor(() => expect(second.result.current.blocked).toBe(true));
    expect(other.result.current.blocked).toBe(false);
    await act(async () => {
      await expect(second.result.current.execute({ kind: 'Jog', y: 10 })).rejects.toThrow('Another movement command is pending');
    });
    expect(apiClient.movePrinter).not.toHaveBeenCalled();
    await act(async () => { finish({ success: true }); await pending; });
    await waitFor(() => expect(second.result.current.blocked).toBe(false));
  });

  it.each(['HTTP 409', 'timeout', 'aborted'])('surfaces %s without retries even with global retries enabled', async message => {
    vi.mocked(apiClient.homePrinter).mockRejectedValue(new Error(message));
    const { result } = setup();
    await act(async () => expect(result.current.execute({ kind: 'HomeAll' })).rejects.toThrow(message));
    await waitFor(() => expect(result.current.blocked).toBe(false));
    expect(apiClient.homePrinter).toHaveBeenCalledOnce();
    expect(apiClient.movePrinter).not.toHaveBeenCalled();
  });

  it('includes actionable uncertainty guidance after a transport failure', async () => {
    vi.mocked(apiClient.homePrinter).mockRejectedValue(new Error('Request timed out'));
    const { result } = setup();
    await act(async () => expect(result.current.execute({ kind: 'HomeAll' })).rejects.toThrow(
      'Check the printer before sending another command; it may have already moved.',
    ));
  });

  it('returns a rejected command without invalidating or retrying', async () => {
    vi.mocked(apiClient.homePrinter).mockResolvedValue({ success: false, error: 'Safety check failed' });
    const { result, client } = setup();
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    await act(async () => expect(await result.current.execute({ kind: 'HomeAll' })).toEqual({ success: false, error: 'Safety check failed' }));
    expect(invalidate).not.toHaveBeenCalled();
    expect(apiClient.homePrinter).toHaveBeenCalledOnce();
  });

  it('does not queue physical commands while offline for reconnect replay', async () => {
    onlineManager.setOnline(false);
    vi.mocked(apiClient.homePrinter).mockRejectedValue(new Error('Network unavailable'));
    const { result } = setup();
    await act(async () => expect(result.current.execute({ kind: 'HomeAll' })).rejects.toThrow('Network unavailable'));
    await act(async () => onlineManager.setOnline(true));
    expect(apiClient.homePrinter).toHaveBeenCalledOnce();
  });

  it('retains only the pending request across unmount, then releases without replay', async () => {
    let finish!: (result: CommandResult) => void;
    vi.mocked(apiClient.homePrinter).mockImplementation(() => new Promise(resolve => { finish = resolve; }));
    const first = setup();
    let pending!: Promise<CommandResult>;
    act(() => { pending = first.result.current.execute({ kind: 'HomeAll' }); });
    await waitFor(() => expect(apiClient.homePrinter).toHaveBeenCalledOnce());
    first.unmount();
    const reopened = renderHook(() => usePrinterMovement({ id: 'printer-1' }), { wrapper: first.wrapper });
    expect(reopened.result.current.blocked).toBe(true);
    await act(async () => { finish({ success: true }); await pending; });
    await waitFor(() => expect(reopened.result.current.blocked).toBe(false));
    expect(apiClient.homePrinter).toHaveBeenCalledOnce();
  });
});
