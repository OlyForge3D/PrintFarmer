import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import { client } from '@/services/api/httpClient';

const route = '/printer-files/printers/printer%201/refresh';

describe('refreshPrinterFileIndex', () => {
  beforeEach(() => vi.restoreAllMocks());
  afterEach(() => vi.useRealTimers());

  it('keeps the default request a schedule nudge that accepts 202', async () => {
    const post = vi.spyOn(client, 'post').mockResolvedValue({ status: 202, data: { code: 'refresh_requested' } });
    await expect(apiClient.refreshPrinterFileIndex('printer 1')).resolves.toBeUndefined();
    expect(post).toHaveBeenCalledExactlyOnceWith(route, undefined, undefined);
  });

  it('forces a synchronous scan with a timeout beyond the server budget', async () => {
    const post = vi.spyOn(client, 'post').mockResolvedValue({ status: 200, data: { code: 'refresh_completed', files: 3 } });
    await expect(apiClient.refreshPrinterFileIndex('printer 1', true)).resolves.toBeUndefined();
    expect(post).toHaveBeenCalledExactlyOnceWith(route, undefined, { params: { force: true }, timeout: 150_000 });
  });

  it.each([
    ['a scheduled 202', { status: 202, data: { code: 'refresh_requested' } }],
    ['a 200 without completion proof', { status: 200, data: {} }],
  ])('rejects %s as an unconfirmed force refresh', async (_case, response) => {
    vi.spyOn(client, 'post').mockResolvedValue(response);
    await expect(apiClient.refreshPrinterFileIndex('printer 1', true))
      .rejects.toThrow('The server did not confirm a completed force refresh');
  });

  it('waits for capacity and returns only after a confirmed force refresh', async () => {
    vi.useFakeTimers();
    vi.spyOn(Math, 'random').mockReturnValue(0);
    const busy = { statusCode: 409, data: { code: 'source_busy' } };
    const post = vi.spyOn(client, 'post')
      .mockRejectedValueOnce(busy)
      .mockRejectedValueOnce(busy)
      .mockResolvedValue({ status: 200, data: { code: 'refresh_completed' } });
    const completed = vi.fn();
    const refresh = apiClient.refreshPrinterFileIndex('printer 1', true).then(completed);

    await vi.advanceTimersByTimeAsync(999);
    expect(post).toHaveBeenCalledTimes(1);
    expect(completed).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1_000);
    expect(post).toHaveBeenCalledTimes(2);
    expect(completed).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    await refresh;
    expect(post).toHaveBeenCalledTimes(3);
    expect(completed).toHaveBeenCalledOnce();
    for (const call of post.mock.calls) {
      expect(call).toEqual([route, undefined, { params: { force: true }, timeout: 150_000 }]);
    }
  });

  it('stops retrying capacity conflicts within two minutes', async () => {
    vi.useFakeTimers();
    vi.spyOn(Math, 'random').mockReturnValue(0);
    const busy = { statusCode: 409, data: { code: 'source_busy' } };
    const post = vi.spyOn(client, 'post').mockRejectedValue(busy);
    const result = expect(apiClient.refreshPrinterFileIndex('printer 1', true)).rejects.toBe(busy);

    await vi.advanceTimersByTimeAsync(120_000);
    await result;
    expect(post).toHaveBeenCalledTimes(120);
    await vi.advanceTimersByTimeAsync(120_000);
    expect(post).toHaveBeenCalledTimes(120);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('does not retry a non-force capacity conflict', async () => {
    const busy = { statusCode: 409, data: { code: 'source_busy' } };
    const post = vi.spyOn(client, 'post').mockRejectedValue(busy);
    await expect(apiClient.refreshPrinterFileIndex('printer 1')).rejects.toBe(busy);
    expect(post).toHaveBeenCalledOnce();
  });

  it.each([
    { statusCode: 409, data: { code: 'waiting_for_confirmed_idle', reasonCode: 'waiting_for_confirmed_idle' } },
    { statusCode: 409, data: { code: 'scan_failed' } },
    { statusCode: 503, data: { code: 'source_busy' } },
    new Error('Network timeout'),
  ])('propagates other failures without replaying the scan: %j', async (deferred) => {
    const post = vi.spyOn(client, 'post').mockRejectedValue(deferred);
    await expect(apiClient.refreshPrinterFileIndex('printer 1', true)).rejects.toBe(deferred);
    expect(post).toHaveBeenCalledOnce();
  });
});
