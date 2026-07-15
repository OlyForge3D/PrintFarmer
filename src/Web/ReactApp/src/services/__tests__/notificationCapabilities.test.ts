import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '../api';

/**
 * Regression tests for `apiClient.getNotificationCapabilities()`.
 *
 * The axios response interceptor in `api.ts` unwraps every `AxiosError` into
 * an `ApiError { statusCode }` before it reaches callers. Any 404-detection
 * code that checks `err.response?.status` is therefore *never* triggered:
 * every legacy server would be misclassified as "capable" and the client
 * would then PUT operator tokens the legacy server rejects with 400, or
 * silently corrupt job-row saves.
 *
 * These tests lock the ApiError-shape detection path so the strip-on-legacy
 * safety story cannot silently regress.
 */
describe('apiClient.getNotificationCapabilities', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  function stubInternalGet(behavior: 'resolve' | 'reject-404' | 'reject-500' | 'reject-network', payload?: unknown) {
    const client = (apiClient as unknown as { client: { get: (...args: unknown[]) => Promise<unknown> } }).client;
    const spy = vi.spyOn(client, 'get').mockImplementation(async () => {
      if (behavior === 'resolve') return { data: payload };
      if (behavior === 'reject-404') {
        return Promise.reject({
          message: 'Not Found',
          statusCode: 404,
          data: undefined,
          isAxiosError: true,
        });
      }
      if (behavior === 'reject-500') {
        return Promise.reject({
          message: 'Server error',
          statusCode: 500,
          data: undefined,
          isAxiosError: true,
        });
      }
      // Network error — no statusCode (or 0/undefined)
      return Promise.reject({
        message: 'Network Error',
        statusCode: 0,
        data: undefined,
        isAxiosError: true,
      });
    });
    return spy;
  }

  it('returns null when the server responds 404 (legacy detection through ApiError.statusCode)', async () => {
    stubInternalGet('reject-404');
    await expect(apiClient.getNotificationCapabilities()).resolves.toBeNull();
  });

  it('returns the capabilities payload when the server responds 200', async () => {
    stubInternalGet('resolve', { supportedEventTypes: ['JobStarted', 'HarvestReady'] });
    await expect(apiClient.getNotificationCapabilities()).resolves.toEqual({
      supportedEventTypes: ['JobStarted', 'HarvestReady'],
    });
  });

  it('rethrows non-404 server errors so the UI can gate save instead of misclassifying as legacy', async () => {
    stubInternalGet('reject-500');
    await expect(apiClient.getNotificationCapabilities()).rejects.toMatchObject({ statusCode: 500 });
  });

  it('rethrows network errors so the UI can gate save instead of misclassifying as legacy', async () => {
    stubInternalGet('reject-network');
    await expect(apiClient.getNotificationCapabilities()).rejects.toMatchObject({ statusCode: 0 });
  });

  it('does NOT rely on the legacy `err.response.status` path (regression: the interceptor already unwraps AxiosError)', async () => {
    // Simulate a raw AxiosError-shape leaking through unmodified — the old
    // code path would have accepted this because it looked at
    // `err.response.status`. If someone reintroduces that check, this test
    // will still pass on the new `err.statusCode` check because the payload
    // below also carries a `statusCode`. To catch a regression to the wrong
    // detection path, we explicitly assert that a shape without `statusCode`
    // but with `response.status: 404` is NOT treated as legacy (the
    // interceptor guarantees no such shape reaches the caller).
    const client = (apiClient as unknown as { client: { get: (...args: unknown[]) => Promise<unknown> } }).client;
    vi.spyOn(client, 'get').mockRejectedValueOnce({
      message: 'Not Found',
      response: { status: 404 },
      // NOTE: no `statusCode` — this is the raw AxiosError shape that never
      // reaches the caller because the api.ts interceptor rewrites it.
    });
    await expect(apiClient.getNotificationCapabilities()).rejects.toMatchObject({
      response: { status: 404 },
    });
  });
});
