import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import { client } from '@/services/api/httpClient';

const route = '/printer-files/printers/printer%201/refresh';

describe('refreshPrinterFileIndex', () => {
  beforeEach(() => vi.restoreAllMocks());

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

  it('propagates a deferred scan without replaying it', async () => {
    const deferred = { statusCode: 409, data: { code: 'waiting_for_confirmed_idle', reasonCode: 'waiting_for_confirmed_idle' } };
    const post = vi.spyOn(client, 'post').mockRejectedValue(deferred);
    await expect(apiClient.refreshPrinterFileIndex('printer 1', true)).rejects.toBe(deferred);
    expect(post).toHaveBeenCalledOnce();
  });
});
