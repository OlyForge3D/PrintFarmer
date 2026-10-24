import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import { client } from '@/services/api/httpClient';

describe('runtime factor HTTP contract', () => {
  beforeEach(() => vi.restoreAllMocks());

  it.each([
    ['setPrinterSpeedFactor', 'speed-factor', 125],
    ['setPrinterFlowFactor', 'flow-factor', 87.5],
  ] as const)('%s posts the documented percentage unchanged', async (method, route, percent) => {
    const post = vi.spyOn(client, 'post').mockResolvedValue({ data: { success: true } });
    await expect(apiClient[method]('p1', percent)).resolves.toEqual({ success: true });
    expect(post).toHaveBeenCalledExactlyOnceWith(`/printers/p1/${route}`, { percent }, { allowAuthReplay: false });
  });

  it.each(['setPrinterSpeedFactor', 'setPrinterFlowFactor'] as const)('%s preserves unknown outcome errors without replay', async method => {
    const failure = { statusCode: 503, message: 'Command outcome unknown' };
    const post = vi.spyOn(client, 'post').mockRejectedValue(failure);
    await expect(apiClient[method]('p1', 125)).rejects.toBe(failure);
    expect(post).toHaveBeenCalledOnce();
  });
});
