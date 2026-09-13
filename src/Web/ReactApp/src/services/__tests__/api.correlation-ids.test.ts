import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { InternalAxiosRequestConfig } from 'axios';
import { ApiClient } from '@/services/api';
import { client } from '@/services/api/httpClient';

const uuidV4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

// Real axios interceptors run, but adapters/fetch never contact a server.
vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: () => 'http://localhost/api',
}));

function responseAdapter(config: InternalAxiosRequestConfig) {
  return Promise.resolve({ config, data: {}, status: 200, statusText: 'OK', headers: {} });
}

describe('HTTP LAN correlation IDs', () => {
  beforeEach(() => {
    vi.stubGlobal('crypto', { getRandomValues: vi.fn(crypto.getRandomValues.bind(crypto)) });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('adds a fresh secure correlation ID to each axios request without replacing idempotency', async () => {
    const adapter = vi.fn(responseAdapter);
    const options = { adapter, headers: { 'Idempotency-Key': 'saved-legacy-operation-key' } };

    await client.post('/test-only', {}, options);
    await client.post('/test-only', {}, options);

    const first = adapter.mock.calls[0][0].headers;
    const second = adapter.mock.calls[1][0].headers;
    expect(first['X-Correlation-Id']).toMatch(uuidV4);
    expect(second['X-Correlation-Id']).toMatch(uuidV4);
    expect(second['X-Correlation-Id']).not.toBe(first['X-Correlation-Id']);
    expect(first['Idempotency-Key']).toBe('saved-legacy-operation-key');
    expect(second['Idempotency-Key']).toBe(first['Idempotency-Key']);
    expect(crypto.getRandomValues).toHaveBeenCalledTimes(2);
  });

  it('adds a fresh secure correlation ID to streaming export attempts', async () => {
    // A controlled HTTP failure lets us inspect the request without downloading a file.
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      new Response('Export unavailable', { status: 503, statusText: 'Unavailable' }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const api = new ApiClient();

    await expect(api.streamExportFile(['printer-1'])).rejects.toThrow('Export failed: 503');
    await expect(api.streamExportFile(['printer-1'])).rejects.toThrow('Export failed: 503');

    const first = new Headers(fetchMock.mock.calls[0][1]?.headers).get('X-Correlation-Id');
    const second = new Headers(fetchMock.mock.calls[1][1]?.headers).get('X-Correlation-Id');
    expect(first).toMatch(uuidV4);
    expect(second).toMatch(uuidV4);
    expect(second).not.toBe(first);
    expect(crypto.getRandomValues).toHaveBeenCalledTimes(2);
  });

  it('rejects axios requests before transport when no secure random source exists', async () => {
    vi.stubGlobal('crypto', {});
    const adapter = vi.fn(responseAdapter);

    await expect(client.get('/test-only', { adapter })).rejects.toMatchObject({
      message: expect.stringContaining('no cryptographically secure random source available'),
    });
    expect(adapter).not.toHaveBeenCalled();
  });

  it('rejects streaming exports before transport when no secure random source exists', async () => {
    vi.stubGlobal('crypto', {});
    const fetchMock = vi.fn<typeof fetch>();
    vi.stubGlobal('fetch', fetchMock);

    await expect(new ApiClient().streamExportFile()).rejects.toThrow(
      'no cryptographically secure random source available',
    );
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
