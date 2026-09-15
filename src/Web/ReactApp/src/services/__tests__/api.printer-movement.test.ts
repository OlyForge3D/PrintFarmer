import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import { client } from '@/services/api/httpClient';

describe('direct printer movement routes', () => {
  beforeEach(() => vi.restoreAllMocks());

  it.each([
    ['homePrinter', 'home'],
    ['homeXY', 'homexy'],
    ['homeZ', 'homez'],
  ] as const)('%s posts directly to %s without an operation identifier', async (method, route) => {
    const post = vi.spyOn(client, 'post').mockResolvedValue({ data: { success: true } });
    await expect(apiClient[method]('printer-1')).resolves.toEqual({ success: true });
    expect(post).toHaveBeenCalledExactlyOnceWith(`/printers/printer-1/${route}`);
  });

  it.each([['movePrinter', 'move'], ['movePrinterTo', 'moveto']] as const)('%s preserves coordinates and rejection outcome', async (method, route) => {
    const outcome = { success: false, error: 'Outside travel limits' };
    const post = vi.spyOn(client, 'post').mockResolvedValue({ data: outcome });
    const move = { x: 10, z: 20, f: 300 };
    await expect(apiClient[method]('printer-1', move)).resolves.toEqual(outcome);
    expect(post).toHaveBeenCalledExactlyOnceWith(`/printers/printer-1/${route}`, move);
  });

  it.each(['HTTP failure', 'timeout', 'aborted'])('propagates %s without replay', async message => {
    const post = vi.spyOn(client, 'post').mockRejectedValue(new Error(message));
    await expect(apiClient.homePrinter('printer-1')).rejects.toThrow(message);
    expect(post).toHaveBeenCalledOnce();
  });
});
